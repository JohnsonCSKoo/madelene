using System.Text;
using Madelene.Filtering;

namespace Madelene.Storage;

/// <summary>
/// An open, immutable segment. Large arrays are memory mapped; centroids, the term dictionary and
/// tombstones live in RAM. Reference counted so retired segments are closed only after the last
/// in-flight query releases them.
/// </summary>
internal sealed unsafe class Segment : IDisposable
{
    private readonly MappedFile _codes;
    private readonly MappedFile _factors;
    private readonly MappedFile _vectors;
    private readonly MappedFile _scales;
    private readonly MappedFile? _time;
    private readonly MappedFile _idData;
    private readonly MappedFile _idOffsets;
    private readonly MappedFile _idHashes;
    private readonly MappedFile _idHashRows;
    private readonly MappedFile _bloom;
    private readonly MappedFile _postings;
    private readonly string[] _terms;
    private readonly Dictionary<string, int> _termIndex;
    private readonly long[] _postOffsets;
    private readonly int[] _postCounts;
    private readonly byte[] _postKinds;
    private int _refCount = 1;
    private bool _retired;
    private int _deleted;

    private Segment(string directory, SegmentMeta meta, long tombstoneGeneration)
    {
        Directory = directory;
        Meta = meta;
        Validate(directory, meta);
        TombstoneGeneration = tombstoneGeneration;
        if (tombstoneGeneration > 0)
        {
            // A referenced tombstone file must exist and be complete; silently treating it as empty would
            // resurrect deleted rows that a replay from the checkpoint will not delete again.
            var tombFile = File(SegmentFileNames.Tombstones(tombstoneGeneration));
            if (!System.IO.File.Exists(tombFile))
                throw new InvalidDataException($"Segment '{directory}' is missing tombstone generation {tombstoneGeneration}.");
            var words = BinaryFiles.Read<ulong>(tombFile);
            if (words.Length != (meta.Rows + 63) >> 6)
                throw new InvalidDataException($"Tombstone file '{tombFile}' has {words.Length} words, expected {(meta.Rows + 63) >> 6}.");
            Tombstones = Bitset.FromWords(meta.Rows, words);
        }
        else
        {
            Tombstones = new Bitset(meta.Rows);
        }
        try
        {
        Centroids = BinaryFiles.Read<float>(File(SegmentFileNames.Centroids));
        ClusterOffsets = BinaryFiles.Read<int>(File(SegmentFileNames.ClusterOffsets));
        _codes = new MappedFile(File(SegmentFileNames.Codes));
        _factors = new MappedFile(File(SegmentFileNames.Factors));
        _vectors = new MappedFile(File(SegmentFileNames.Vectors));
        _scales = new MappedFile(File(SegmentFileNames.Scales));
        _time = meta.HasTime ? new MappedFile(File(SegmentFileNames.Time)) : null;
        _idData = new MappedFile(File(SegmentFileNames.IdData));
        _idOffsets = new MappedFile(File(SegmentFileNames.IdOffsets));
        _idHashes = new MappedFile(File(SegmentFileNames.IdHashes));
        _idHashRows = new MappedFile(File(SegmentFileNames.IdHashRows));
        _bloom = new MappedFile(File(SegmentFileNames.Bloom));
        _postings = new MappedFile(File(SegmentFileNames.Postings));

        var termBytes = System.IO.File.ReadAllBytes(File(SegmentFileNames.TermData));
        var termOffsets = BinaryFiles.Read<long>(File(SegmentFileNames.TermOffsets));
        _terms = new string[termOffsets.Length - 1];
        _termIndex = new Dictionary<string, int>(_terms.Length, StringComparer.Ordinal);
        for (var t = 0; t < _terms.Length; t++)
        {
            _terms[t] = Encoding.UTF8.GetString(termBytes, (int)termOffsets[t], (int)(termOffsets[t + 1] - termOffsets[t]));
            _termIndex[_terms[t]] = t;
        }

        _postOffsets = BinaryFiles.Read<long>(File(SegmentFileNames.PostingOffsets));
        _postCounts = BinaryFiles.Read<int>(File(SegmentFileNames.PostingCounts));
        _postKinds = BinaryFiles.Read<byte>(File(SegmentFileNames.PostingKinds));

        _deleted = Tombstones.Count();
            ValidateContents();
        }
        catch
        {
            CloseFiles(); // don't leave files locked by maps of a segment that failed to open
            throw;
        }
    }

    private void ValidateContents()
    {
        var offsets = ClusterOffsets;
        if (offsets[0] != 0 || offsets[^1] != Rows)
            throw new InvalidDataException($"Segment '{Directory}' has inconsistent cluster offsets.");
        for (var c = 0; c + 1 < offsets.Length; c++)
            if (offsets[c] > offsets[c + 1])
                throw new InvalidDataException($"Segment '{Directory}' has non-monotonic cluster offsets.");
        var idOffsets = _idOffsets.As<long>();
        if (idOffsets[0] != 0 || idOffsets[Rows] != _idData.Length)
            throw new InvalidDataException($"Segment '{Directory}' has id offsets that do not match ids.dat.");
        if (_bloom.Length < 8 || _bloom.Length % 8 != 0)
            throw new InvalidDataException($"Segment '{Directory}' has a malformed bloom filter.");
        if (_postOffsets.Length != _terms.Length || _postCounts.Length != _terms.Length || _postKinds.Length != _terms.Length)
            throw new InvalidDataException($"Segment '{Directory}' has a posting directory that does not match its terms.");
        var words = ((long)Rows + 63) >> 6;
        for (var t = 0; t < _terms.Length; t++)
        {
            var bytes = (PostingKind)_postKinds[t] == PostingKind.Bitset ? words * 8 : (long)_postCounts[t] * 4;
            if (_postOffsets[t] < 0 || _postOffsets[t] + bytes > _postings.Length)
                throw new InvalidDataException($"Segment '{Directory}' has a posting list outside postings.bin.");
        }
    }

    /// <summary>Checks every file against the row/cluster counts, so truncated files fail cleanly instead of faulting.</summary>
    private static void Validate(string directory, SegmentMeta meta)
    {
        if (meta.Format != SegmentMeta.CurrentFormat)
            throw new InvalidDataException($"Segment '{directory}' has format {meta.Format}; this version reads format {SegmentMeta.CurrentFormat}. Rebuild the index.");
        long rows = meta.Rows, clusters = meta.Clusters, dims = meta.Dimensions;
        var expected = new List<(string Name, long Bytes)>
        {
            (SegmentFileNames.Centroids, clusters * dims * 4),
            (SegmentFileNames.ClusterOffsets, (clusters + 1) * 4),
            (SegmentFileNames.Codes, rows * meta.CodeBytes),
            (SegmentFileNames.Factors, rows * 4),
            (SegmentFileNames.Vectors, rows * dims),
            (SegmentFileNames.Scales, rows * 4),
            (SegmentFileNames.IdOffsets, (rows + 1) * 8),
            (SegmentFileNames.IdHashes, rows * 8),
            (SegmentFileNames.IdHashRows, rows * 4),
        };
        if (meta.HasTime)
            expected.Add((SegmentFileNames.Time, rows * 8));
        foreach (var (name, bytes) in expected)
        {
            var path = Path.Combine(directory, name);
            var actual = System.IO.File.Exists(path) ? new FileInfo(path).Length : -1;
            if (actual != bytes)
                throw new InvalidDataException($"Segment file '{path}' is {actual} bytes, expected {bytes}.");
        }

        foreach (var name in new[] { SegmentFileNames.IdData, SegmentFileNames.Bloom, SegmentFileNames.TermData, SegmentFileNames.TermOffsets,
                     SegmentFileNames.PostingOffsets, SegmentFileNames.PostingCounts, SegmentFileNames.PostingKinds, SegmentFileNames.Postings })
        {
            if (!System.IO.File.Exists(Path.Combine(directory, name)))
                throw new InvalidDataException($"Segment file '{Path.Combine(directory, name)}' is missing.");
        }
    }

    public static Segment Open(string directory, long tombstoneGeneration) =>
        new(directory, SegmentMeta.Load(Path.Combine(directory, SegmentFileNames.Meta)), tombstoneGeneration);

    public string Directory { get; }
    public SegmentMeta Meta { get; }
    public long Id => Meta.Id;
    public int Rows => Meta.Rows;
    public int Clusters => Meta.Clusters;
    public int CodeBytes => Meta.CodeBytes;
    public int Dimensions => Meta.Dimensions;

    /// <summary>Row-major [clusters × dims].</summary>
    public float[] Centroids { get; }

    /// <summary>Cluster c owns rows [ClusterOffsets[c], ClusterOffsets[c+1]).</summary>
    public int[] ClusterOffsets { get; }

    public Bitset Tombstones { get; }
    public long TombstoneGeneration { get; private set; }
    public bool TombstonesDirty { get; private set; }
    public int DeletedRows => Volatile.Read(ref _deleted);
    public int LiveRows => Rows - DeletedRows;
    public IReadOnlyList<string> Terms => _terms;

    /// <summary>Per field-and-kind sorted numeric term values, built lazily for range filters.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, (double[] Values, int[] Terms)> OrderedTermCache { get; } = new(StringComparer.Ordinal);

    public byte* CodesPointer => _codes.Pointer;
    public float* Factors => _factors.As<float>();
    public sbyte* Vectors => _vectors.As<sbyte>();
    public float* Scales => _scales.As<float>();
    public long* Times => _time != null ? _time.As<long>() : null;

    public long TimeAt(int row) => _time != null ? Times[row] : long.MinValue;

    public bool OverlapsTime(long? lower, long? upper)
    {
        if (!Meta.HasTime)
            return true;
        if (Meta.MinTime == long.MinValue)
            return false; // no row has a time value, so no time predicate can match
        if (lower is { } lo && Meta.MaxTime < lo)
            return false;
        if (upper is { } hi && Meta.MinTime > hi)
            return false;
        return true;
    }

    public string GetId(int row)
    {
        var offsets = _idOffsets.As<long>();
        var start = offsets[row];
        var length = (int)(offsets[row + 1] - start);
        return Encoding.UTF8.GetString(_idData.Pointer + start, length);
    }

    /// <summary>Returns the row holding <paramref name="id"/>, or -1. Deleted rows are still found.</summary>
    public int FindRow(string id, ulong hash)
    {
        if (!Bloom.MayContain(_bloom.As<ulong>(), _bloom.Length / 8, hash))
            return -1;
        var hashes = _idHashes.As<ulong>();
        var rows = _idHashRows.As<int>();
        long lo = 0, hi = Rows - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var h = hashes[mid];
            if (h < hash)
                lo = mid + 1;
            else if (h > hash)
                hi = mid - 1;
            else
            {
                // walk back to the first equal hash, then check every collision candidate
                while (mid > 0 && hashes[mid - 1] == hash)
                    mid--;
                for (; mid < Rows && hashes[mid] == hash; mid++)
                    if (GetId(rows[mid]) == id)
                        return rows[mid];
                return -1;
            }
        }

        return -1;
    }

    /// <summary>Marks a row deleted. Returns true if it was live.</summary>
    public bool Delete(int row)
    {
        if (!Tombstones.SetAtomic(row))
            return false;
        Interlocked.Increment(ref _deleted);
        TombstonesDirty = true;
        return true;
    }

    /// <summary>
    /// Writes the tombstones under a new generation. The previous generation's file is kept until the
    /// manifest that references the new one is durable; call <see cref="DeleteTombstoneGeneration"/> then.
    /// </summary>
    public long SaveTombstones(long generation)
    {
        var tmp = File(SegmentFileNames.Tombstones(generation)) + ".tmp";
        BinaryFiles.Write<ulong>(tmp, Tombstones.Words);
        FileRetry.Run(() => System.IO.File.Move(tmp, File(SegmentFileNames.Tombstones(generation)), overwrite: true));
        var previous = TombstoneGeneration;
        TombstoneGeneration = generation;
        TombstonesDirty = false;
        return previous;
    }

    public void DeleteTombstoneGeneration(long generation)
    {
        if (generation > 0 && generation != TombstoneGeneration)
            TryDelete(File(SegmentFileNames.Tombstones(generation)));
    }

    /// <summary>Calls <paramref name="action"/> for every row in the term's posting list.</summary>
    public void ForEachPosting(int term, Action<int> action)
    {
        var offset = _postOffsets[term];
        if ((PostingKind)_postKinds[term] == PostingKind.Bitset)
        {
            var words = (ulong*)(_postings.Pointer + offset);
            var wordCount = (Rows + 63) >> 6;
            for (var w = 0; w < wordCount; w++)
            {
                var bits = words[w];
                while (bits != 0)
                {
                    action((w << 6) + System.Numerics.BitOperations.TrailingZeroCount(bits));
                    bits &= bits - 1;
                }
            }
        }
        else
        {
            var rows = (int*)(_postings.Pointer + offset);
            var count = _postCounts[term];
            for (var i = 0; i < count; i++)
                action(rows[i]);
        }
    }

    public int TermIndex(string key) => _termIndex.TryGetValue(key, out var t) ? t : -1;

    public int PostingCount(int term) => _postCounts[term];

    /// <summary>Range of term indexes [start, end) whose keys begin with <paramref name="prefix"/>.</summary>
    public (int Start, int End) TermPrefixRange(string prefix)
    {
        var start = LowerBound(prefix);
        var end = start;
        while (end < _terms.Length && _terms[end].StartsWith(prefix, StringComparison.Ordinal))
            end++;
        return (start, end);
    }

    public void OrPostings(int term, Bitset target)
    {
        var offset = _postOffsets[term];
        var count = _postCounts[term];
        if ((PostingKind)_postKinds[term] == PostingKind.Bitset)
        {
            var words = (ulong*)(_postings.Pointer + offset);
            target.OrWords(new ReadOnlySpan<ulong>(words, target.Words.Length));
        }
        else
        {
            var rows = (int*)(_postings.Pointer + offset);
            for (var i = 0; i < count; i++)
                target.Set(rows[i]);
        }
    }

    public bool TryAddRef()
    {
        while (true)
        {
            var current = Volatile.Read(ref _refCount);
            if (current == 0)
                return false;
            if (Interlocked.CompareExchange(ref _refCount, current + 1, current) == current)
                return true;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _refCount) == 0)
            CloseFiles();
    }

    /// <summary>Drops the index's own reference; files close once in-flight queries finish.</summary>
    public void Retire(bool deleteFiles)
    {
        if (_retired)
            return;
        _retired = true;
        DeleteOnClose = deleteFiles;
        Release();
    }

    private bool DeleteOnClose { get; set; }

    public void Dispose() => Retire(deleteFiles: false);

    private void CloseFiles()
    {
        // Null-safe: also used when the constructor fails half way.
        _codes?.Dispose();
        _factors?.Dispose();
        _vectors?.Dispose();
        _scales?.Dispose();
        _time?.Dispose();
        _idData?.Dispose();
        _idOffsets?.Dispose();
        _idHashes?.Dispose();
        _idHashRows?.Dispose();
        _bloom?.Dispose();
        _postings?.Dispose();
        if (DeleteOnClose)
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
                // Left for the garbage collector pass at next open.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private int LowerBound(string key)
    {
        int lo = 0, hi = _terms.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (string.CompareOrdinal(_terms[mid], key) < 0)
                lo = mid + 1;
            else
                hi = mid;
        }

        return lo;
    }

    private string File(string name) => Path.Combine(Directory, name);

    private static void TryDelete(string path)
    {
        try
        {
            System.IO.File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
