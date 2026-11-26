using Madelene.Filtering;
using Madelene.Numerics;
using Madelene.Quantization;
using Madelene.Storage;

namespace Madelene;

public sealed partial class VectorIndex
{
    /// <summary>
    /// Merges segments that share a time bucket once there are <see cref="MadeleneIndexOptions.MergeFactor"/>
    /// of them, and rewrites segments whose deleted fraction exceeds the threshold. Merged segments that
    /// reach <see cref="MadeleneIndexOptions.IvfMinRows"/> get an IVF layer. Writes keep flowing while
    /// segments are rebuilt; deletes that land meanwhile are carried over before the swap.
    /// </summary>
    /// <returns>Number of segments replaced.</returns>
    public int Compact(bool force = false)
    {
        lock (_compactLock)
        {
            var replaced = 0;
            foreach (var group in PlanCompaction(force))
            {
                var merged = CompactGroup(group);
                replaced += merged;
                if (merged > 0 && Definition.Options.ReleaseMemoryAfterCompaction)
                    ReleaseCompactionMemory();
            }

            return replaced;
        }
    }

    private List<Segment[]> PlanCompaction(bool force)
    {
        var options = Definition.Options;
        var plan = new List<Segment[]>();
        var segments = _state.Segments;
        foreach (var bucket in segments.GroupBy(s => s.Meta.Bucket))
        {
            var members = bucket.ToArray();
            if (members.Length >= options.MergeFactor || (force && members.Length > 1))
            {
                plan.Add(members);
                continue;
            }

            foreach (var seg in members)
                if ((double)seg.DeletedRows / seg.Rows >= options.TombstoneRewriteRatio || (force && seg.DeletedRows > 0))
                    plan.Add([seg]);
        }

        return plan;
    }

    private int CompactGroup(Segment[] group)
    {
        Bitset[] snapshot;
        long newId;
        lock (_writeLock)
        {
            ThrowIfDisposed();
            if (group.Any(s => !_state.Segments.Contains(s)))
                return 0;
            snapshot = group.Select(s => s.Tombstones.Clone()).ToArray();
            newId = _manifest.NextSegmentId++;
            foreach (var seg in group)
                seg.TryAddRef();
        }

        try
        {
            var rows = new List<BuildRow>();
            for (var i = 0; i < group.Length; i++)
                rows.AddRange(ReadLiveRows(group[i], snapshot[i]));

            Segment? merged = null;
            if (rows.Count > 0)
            {
                var dir = SegmentBuilder.Build(Directory, newId, group[0].Meta.Bucket, rows, Definition, _rotation);
                merged = Segment.Open(dir, tombstoneGeneration: 0);
            }

            lock (_writeLock)
            {
                // The group may have been dropped meanwhile (retention, dispose, drop): publishing the
                // merged segment would resurrect its rows or write into a dead index.
                if (_disposed || group.Any(s => !_state.Segments.Contains(s)))
                {
                    merged?.Retire(deleteFiles: true);
                    return 0;
                }

                // Carry over deletes that happened while we were building.
                if (merged != null)
                {
                    for (var i = 0; i < group.Length; i++)
                    {
                        var late = group[i].Tombstones.Clone();
                        late.AndNot(snapshot[i]);
                        foreach (var row in late.EnumerateSet())
                        {
                            var id = group[i].GetId(row);
                            var newRow = merged.FindRow(id, Hashing.Hash64(id));
                            if (newRow >= 0)
                                merged.Delete(newRow);
                        }
                    }
                }

                var current = _state;
                var remaining = current.Segments.Where(s => !group.Contains(s));
                _state = current with { Segments = merged == null ? remaining.ToArray() : [.. remaining, merged] };
                PersistLocked(checkpoint: null);
                foreach (var seg in group)
                    seg.Retire(deleteFiles: true);
            }

            return group.Length;
        }
        finally
        {
            foreach (var seg in group)
                seg.Release();
        }
    }

    /// <summary>
    /// A merge allocates ~1 KB per row and drops it all once the new segment is published. The GC keeps that
    /// memory committed for reuse (with Server GC, per heap), so a process that compacts once a day would hold
    /// gigabytes it doesn't use. A blocking, compacting, aggressive collection hands it back to the OS. The live
    /// heap is small at that point (tens to hundreds of MB), so the pause is short.
    /// </summary>
    private static void ReleaseCompactionMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    /// <summary>Reconstructs build rows (int8-decoded vectors, terms, time) for the live rows of a segment.</summary>
    private unsafe List<BuildRow> ReadLiveRows(Segment seg, Bitset deleted)
    {
        var termsPerRow = new List<string>?[seg.Rows];
        for (var t = 0; t < seg.Terms.Count; t++)
        {
            var term = seg.Terms[t];
            seg.ForEachPosting(t, row =>
            {
                if (!deleted.Get(row))
                    (termsPerRow[row] ??= []).Add(term);
            });
        }

        var dims = seg.Dimensions;
        var rows = new List<BuildRow>(seg.Rows - deleted.Count());
        for (var row = 0; row < seg.Rows; row++)
        {
            if (deleted.Get(row))
                continue;
            // Kept as int8 (768 B instead of 3 KB of floats); the builder decodes it per row.
            var terms = termsPerRow[row];
            rows.Add(new BuildRow
            {
                Id = seg.GetId(row),
                Int8 = new ReadOnlySpan<sbyte>(seg.Vectors + (long)row * dims, dims).ToArray(),
                Int8Scale = seg.Scales[row],
                NormalizeDecoded = Definition.Similarity == VectorSimilarity.Cosine,
                Terms = terms == null ? [] : terms.ToArray(),
                Time = seg.TimeAt(row),
            });
        }

        return rows;
    }
}
