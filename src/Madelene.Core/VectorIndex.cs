using System.Diagnostics;
using System.Text.Json;
using Madelene.Filtering;
using Madelene.Indexing;
using Madelene.Model;
using Madelene.Numerics;
using Madelene.Quantization;
using Madelene.Search;
using Madelene.Storage;

namespace Madelene;

/// <summary>
/// One vector index: an in-memory write buffer plus immutable, time-bucketed segments on disk.
/// Single writer (all mutations are serialized), many concurrent readers.
/// </summary>
public sealed partial class VectorIndex : IDisposable
{
    internal const string DefinitionFile = "index.json";
    internal const string RotationFile = "rotation.f32";
    private const int MaxRangeRows = 16_384;

    private readonly object _writeLock = new();
    private readonly object _compactLock = new();
    private readonly RandomRotation _rotation;
    private readonly Manifest _manifest;
    private volatile IndexState _state;
    private volatile bool _disposed;

    private VectorIndex(string directory, VectorIndexDefinition definition, RandomRotation rotation, Manifest manifest, Segment[] segments)
    {
        Directory = directory;
        Definition = definition;
        _rotation = rotation;
        _manifest = manifest;
        _state = new IndexState(segments, new MemTable(), []);
    }

    public string Directory { get; }

    public VectorIndexDefinition Definition { get; }

    /// <summary>The host checkpoint (e.g. change-stream resume token) persisted with the current durable state.</summary>
    public string? Checkpoint => _manifest.Checkpoint;

    internal IReadOnlyList<Segment> Segments => _state.Segments;

    public static VectorIndex Create(string directory, VectorIndexDefinition definition)
    {
        definition.Validate();
        System.IO.Directory.CreateDirectory(directory);
        if (File.Exists(Path.Combine(directory, DefinitionFile)))
            throw new InvalidOperationException($"An index already exists in '{directory}'.");
        var rotation = RandomRotation.Create(definition.Dimensions, definition.Options.RotationSeed);
        rotation.Save(Path.Combine(directory, RotationFile));
        BinaryFiles.WriteDurable(Path.Combine(directory, DefinitionFile), System.Text.Encoding.UTF8.GetBytes(definition.ToJson()));
        var manifest = new Manifest();
        manifest.Save(directory);
        return new VectorIndex(directory, definition, rotation, manifest, []);
    }

    public static VectorIndex Open(string directory, string name)
    {
        var definition = VectorIndexDefinition.FromJson(name, File.ReadAllText(Path.Combine(directory, DefinitionFile)));
        var rotation = RandomRotation.Load(Path.Combine(directory, RotationFile), definition.Dimensions);
        var manifest = Manifest.LoadOrCreate(directory);
        CollectGarbage(directory, manifest);
        var segments = new List<Segment>();
        try
        {
            foreach (var s in manifest.Segments)
                segments.Add(Segment.Open(Path.Combine(directory, SegmentBuilder.SegmentDirectoryName(s.Id)), s.TombstoneGeneration));
        }
        catch
        {
            foreach (var seg in segments)
                seg.Retire(deleteFiles: false); // release memory maps so the files stay usable
            throw;
        }

        return new VectorIndex(directory, definition, rotation, manifest, segments.ToArray());
    }

    // ------------------------------------------------------------------ writes

    /// <summary>Inserts or replaces a document (by id).</summary>
    public void Upsert(IndexDocument document)
    {
        var row = Prepare(document);
        lock (_writeLock)
        {
            ThrowIfDisposed();
            var state = _state;
            var hash = Hashing.Hash64(row.Id);
            // Tombstone the old version before the new one becomes visible; readers de-duplicate the overlap.
            DeleteFromSegments(state, row.Id, hash);
            foreach (var frozen in state.Frozen)
                frozen.Delete(row.Id);
            state.Active.Upsert(row);
            if (state.Active.LiveCount >= Definition.Options.SealRows)
                SealLocked(checkpoint: null);
        }
    }

    /// <summary>
    /// Adds a document whose id the caller guarantees is not in any sealed segment (e.g. a backfill into an
    /// empty index). Skips the per-segment id lookup that <see cref="Upsert"/> pays, which grows with the
    /// segment count. Duplicates within the write buffer are still replaced.
    /// </summary>
    public void Insert(IndexDocument document)
    {
        var row = Prepare(document);
        lock (_writeLock)
        {
            ThrowIfDisposed();
            var state = _state;
            state.Active.Upsert(row);
            if (state.Active.LiveCount >= Definition.Options.SealRows)
                SealLocked(checkpoint: null);
        }
    }

    public void UpsertMany(IEnumerable<IndexDocument> documents)
    {
        foreach (var doc in documents)
            Upsert(doc);
    }

    /// <summary>Deletes a document by id. Returns true if a live copy was found.</summary>
    public bool Delete(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        lock (_writeLock)
        {
            ThrowIfDisposed();
            var state = _state;
            var found = state.Active.Delete(id);
            foreach (var frozen in state.Frozen)
                found |= frozen.Delete(id);
            found |= DeleteFromSegments(state, id, Hashing.Hash64(id));
            return found;
        }
    }

    /// <summary>
    /// Makes every write so far durable and records <paramref name="checkpoint"/> with it.
    /// After a crash, replay the host's change feed from <see cref="Checkpoint"/>; writes are idempotent.
    /// </summary>
    public void Flush(string? checkpoint = null)
    {
        lock (_writeLock)
        {
            ThrowIfDisposed();
            if (_state.Active.LiveCount > 0)
                SealLocked(checkpoint);
            else
                PersistLocked(checkpoint);
        }
    }

    /// <summary>Removes every document whose time field is older than <paramref name="cutoffUtc"/>.</summary>
    public long DeleteOlderThan(DateTime cutoffUtc)
    {
        if (Definition.TimePath == null)
            throw new InvalidOperationException("Retention needs a timeField in the index definition.");
        var cutoff = FieldValue.ToUnixMs(cutoffUtc);
        long removed = 0;
        lock (_writeLock)
        {
            ThrowIfDisposed();
            var state = _state;
            removed += state.Active.DeleteWhere(r => r.Time != long.MinValue && r.Time < cutoff);
            foreach (var frozen in state.Frozen)
                removed += frozen.DeleteWhere(r => r.Time != long.MinValue && r.Time < cutoff);

            var keep = new List<Segment>();
            var drop = new List<Segment>();
            foreach (var seg in state.Segments)
            {
                if (seg.Meta.HasTime && seg.Meta.MaxTime != long.MinValue && seg.Meta.MaxTime < cutoff)
                {
                    removed += seg.LiveRows;
                    drop.Add(seg);
                    continue;
                }

                keep.Add(seg);
                if (!seg.Meta.HasTime || seg.Meta.MinTime == long.MinValue || seg.Meta.MinTime >= cutoff)
                    continue;
                for (var r = 0; r < seg.Rows; r++)
                {
                    var t = seg.TimeAt(r);
                    if (t != long.MinValue && t < cutoff && seg.Delete(r))
                        removed++;
                }
            }

            if (drop.Count > 0)
            {
                _state = state with { Segments = keep.ToArray() };
                PersistLocked(checkpoint: null);
                foreach (var seg in drop)
                    seg.Retire(deleteFiles: true);
            }
            else
            {
                PersistLocked(checkpoint: null);
            }
        }

        return removed;
    }

    private bool DeleteFromSegments(IndexState state, string id, ulong hash)
    {
        var found = false;
        foreach (var seg in state.Segments)
        {
            var row = seg.FindRow(id, hash);
            if (row >= 0 && seg.Delete(row))
                found = true;
        }

        return found;
    }

    private MemRow Prepare(IndexDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Vector.Length != Definition.Dimensions)
            throw new ArgumentException($"Document '{document.Id}' has {document.Vector.Length} dimensions, index '{Definition.Name}' expects {Definition.Dimensions}.");
        var vector = (float[])document.Vector.Clone();
        if (Definition.Similarity == VectorSimilarity.Cosine)
            VectorMath.NormalizeInPlace(vector);

        var fields = new Dictionary<string, IReadOnlyList<FieldValue>>(StringComparer.Ordinal);
        var terms = new List<string>();
        foreach (var path in Definition.FilterPaths)
        {
            if (!document.Fields.TryGetValue(path, out var values) || values.Count == 0)
                continue;
            fields[path] = values;
            foreach (var value in values.Distinct())
                terms.Add(TermKeys.Of(path, value));
        }

        return new MemRow
        {
            Id = document.Id,
            Vector = vector,
            Fields = fields,
            Terms = terms,
            Time = Definition.TimePath != null && document.TimeUnixMs is { } t ? t : long.MinValue,
        };
    }

    private void SealLocked(string? checkpoint)
    {
        var state = _state;
        var frozen = state.Active;
        _state = state with { Active = new MemTable(), Frozen = [.. state.Frozen, frozen] };

        var rows = frozen.LiveRows();
        var created = new List<Segment>();
        var bucketMs = System.Math.Max(1L, (long)Definition.Options.Bucket.TotalMilliseconds);
        try
        {
            foreach (var group in rows.GroupBy(r => r.Time == long.MinValue ? long.MinValue : FloorDiv(r.Time, bucketMs)).OrderBy(g => g.Key))
            {
                var id = _manifest.NextSegmentId++;
                var dir = SegmentBuilder.Build(Directory, id, group.Key, group.Select(r => r.ToBuildRow()).ToList(), Definition, _rotation);
                created.Add(Segment.Open(dir, tombstoneGeneration: 0));
            }
        }
        catch
        {
            // e.g. disk full: put the buffer back so nothing is lost and later checkpoints can't skip it.
            // No other write can have happened meanwhile (we hold the write lock).
            foreach (var seg in created)
                seg.Retire(deleteFiles: true);
            var failed = _state;
            _state = failed with { Active = frozen, Frozen = failed.Frozen.Where(f => !ReferenceEquals(f, frozen)).ToArray() };
            throw;
        }

        var current = _state;
        _state = current with
        {
            Segments = [.. current.Segments, .. created],
            Frozen = current.Frozen.Where(f => !ReferenceEquals(f, frozen)).ToArray(),
        };
        PersistLocked(checkpoint);
    }

    private static long FloorDiv(long a, long b) => a >= 0 ? a / b : -((-a + b - 1) / b);

    private void PersistLocked(string? checkpoint)
    {
        var generation = _manifest.NextGeneration++;
        var obsolete = new List<(Segment Segment, long Generation)>();
        foreach (var seg in _state.Segments)
        {
            if (seg.TombstonesDirty)
                obsolete.Add((seg, seg.SaveTombstones(generation)));
        }

        _manifest.Segments = _state.Segments.Select(s => new ManifestSegment(s.Id, s.TombstoneGeneration)).ToList();
        if (checkpoint != null)
        {
            _manifest.Checkpoint = checkpoint;
            _manifest.CheckpointUtc = DateTime.UtcNow;
        }

        _manifest.Save(Directory);
        foreach (var (seg, previous) in obsolete)
            seg.DeleteTombstoneGeneration(previous);
    }

    private static void CollectGarbage(string directory, Manifest manifest)
    {
        var live = manifest.Segments.ToDictionary(s => SegmentBuilder.SegmentDirectoryName(s.Id), s => s.TombstoneGeneration);
        foreach (var dir in System.IO.Directory.EnumerateDirectories(directory, "seg-*"))
        {
            var name = Path.GetFileName(dir);
            if (!live.TryGetValue(name, out var generation))
            {
                System.IO.Directory.Delete(dir, recursive: true);
                continue;
            }

            foreach (var tomb in System.IO.Directory.EnumerateFiles(dir, "tomb-*"))
            {
                if (Path.GetFileName(tomb) != SegmentFileNames.Tombstones(generation))
                    File.Delete(tomb);
            }
        }
    }

    // ------------------------------------------------------------------ info

    public IndexStatistics GetStatistics()
    {
        var state = _state;
        long bytes = 0;
        foreach (var seg in state.Segments)
            bytes += new DirectoryInfo(seg.Directory).EnumerateFiles().Sum(f => f.Length);
        return new IndexStatistics
        {
            Segments = state.Segments.Length,
            SegmentRows = state.Segments.Sum(s => (long)s.Rows),
            LiveSegmentRows = state.Segments.Sum(s => (long)s.LiveRows),
            IvfSegments = state.Segments.Count(s => s.Clusters > 1),
            BufferedRows = state.Active.LiveCount + state.Frozen.Sum(f => f.LiveCount),
            DiskBytes = bytes,
            Checkpoint = _manifest.Checkpoint,
        };
    }

    public void Dispose()
    {
        lock (_writeLock)
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (var seg in _state.Segments)
                seg.Retire(deleteFiles: false);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    internal sealed record IndexState(Segment[] Segments, MemTable Active, MemTable[] Frozen);
}

public sealed record IndexStatistics
{
    public int Segments { get; init; }
    public int IvfSegments { get; init; }
    public long SegmentRows { get; init; }
    public long LiveSegmentRows { get; init; }
    public long BufferedRows { get; init; }
    public long DiskBytes { get; init; }
    public string? Checkpoint { get; init; }

    public override string ToString() => JsonSerializer.Serialize(this);
}
