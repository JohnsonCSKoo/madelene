using System.Collections.Concurrent;
using System.Diagnostics;
using Madelene.Filtering;
using Madelene.Indexing;
using Madelene.Numerics;
using Madelene.Quantization;
using Madelene.Search;
using Madelene.Storage;

namespace Madelene;

public sealed partial class VectorIndex
{
    /// <summary>Extra results kept before de-duplication so duplicates can't push hits below `limit`.</summary>
    private const int DuplicateSlack = 32;

    /// <summary>
    /// Runs a <c>$vectorSearch</c>. Approximate search scores 1-bit codes for the candidate pool, then
    /// rescores the pool with int8 vectors; the write buffer is always scored exactly.
    /// </summary>
    /// <exception cref="FilterNotSupportedException">The filter references a field that is not indexed as a filter.</exception>
    public VectorSearchResult Search(VectorSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (request.Path != Definition.VectorPath)
            throw new VectorSearchRequestException($"path '{request.Path}' does not match the index vector path '{Definition.VectorPath}'.");
        if (request.QueryVector.Length != Definition.Dimensions)
            throw new VectorSearchRequestException($"queryVector has {request.QueryVector.Length} dimensions, the index expects {Definition.Dimensions}.");
        ValidateFilterPaths(request.Filter);

        var sw = Stopwatch.StartNew();
        var query = (float[])request.QueryVector.Clone();
        if (Definition.Similarity == VectorSimilarity.Cosine)
            VectorMath.NormalizeInPlace(query);

        // Pin every segment of one consistent snapshot. If a segment was retired and closed between
        // reading the snapshot and pinning it, a newer snapshot (with its replacement) exists: retry.
        IndexState state;
        var segments = new List<Segment>();
        while (true)
        {
            ThrowIfDisposed(); // a disposed index has only closed segments; don't spin
            state = _state;
            var complete = true;
            foreach (var seg in state.Segments)
            {
                if (seg.TryAddRef())
                {
                    segments.Add(seg);
                    continue;
                }

                complete = false;
                break;
            }

            if (complete)
                break;
            foreach (var seg in segments)
                seg.Release();
            segments.Clear();
        }

        try
        {
            return Execute(request, query, state, segments, sw);
        }
        finally
        {
            foreach (var seg in segments)
                seg.Release();
        }
    }

    private VectorSearchResult Execute(VectorSearchRequest request, float[] query, IndexState state, List<Segment> segments, Stopwatch sw)
    {
        var options = Definition.Options;
        var (lower, upper) = TimeBounds(request.Filter, Definition.TimePath);

        var rotated = new float[Definition.Dimensions];
        _rotation.Apply(query, rotated);
        var lut = new float[BinaryCodec.CodeBytes(Definition.Dimensions) * 256];
        var querySum = BinaryCodec.BuildLookup(rotated, lut);

        var phase = Stopwatch.StartNew();

        // ---- filter: prune by time, then evaluate the filter into row masks (minus tombstones), segments in parallel
        var inRange = new List<Segment>(segments.Count);
        foreach (var seg in segments)
            if ((lower == null && upper == null) || seg.OverlapsTime(lower, upper))
                inRange.Add(seg);
        var pruned = segments.Count - inRange.Count;

        var evaluated = new (Bitset? Mask, int Matched)[inRange.Count];
        Parallel.For(0, inRange.Count, i =>
        {
            var seg = inRange[i];
            var mask = new SegmentFilterEvaluator(seg, Definition.TimePath).Evaluate(request.Filter);
            if (seg.DeletedRows > 0)
            {
                mask ??= new Bitset(seg.Rows, fill: true);
                mask.AndNot(seg.Tombstones);
            }

            evaluated[i] = (mask, mask?.Count() ?? seg.Rows);
        });

        var candidatesBySegment = new List<(Segment Segment, Bitset? Mask, int Matched)>();
        long matchedTotal = 0;
        for (var i = 0; i < inRange.Count; i++)
        {
            if (evaluated[i].Matched == 0)
                continue;
            matchedTotal += evaluated[i].Matched;
            candidatesBySegment.Add((inRange[i], evaluated[i].Mask, evaluated[i].Matched));
        }

        // The 1-bit pool that gets int8-rescored: numCandidates, raised to the floor and to a fraction of the
        // matching rows (more rows crowd the top-k boundary). Capped at MongoDB's 10,000, or at
        // RescorePoolPerLimit × limit when that is larger, so large limits still get a deep enough pool.
        var poolCap = System.Math.Max(VectorSearchRequest.MaxCandidates, (long)request.Limit * options.RescorePoolPerLimit);
        var poolSize = request.Exact
            ? request.Limit + DuplicateSlack
            : (int)System.Math.Min(poolCap, System.Math.Max(
                System.Math.Max(request.NumCandidates!.Value, options.MinRescorePool),
                (long)(matchedTotal * options.RescorePoolFraction)));

        var filterMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();

        // ---- plan: flat scan for small or exact segments; one shared IVF probe budget for the rest
        var (work, flat, ivf) = Plan(request, query, candidatesBySegment, poolSize);

        var planMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();

        // ---- stage 1: score codes (or int8 when exact) across all work items in parallel
        var pool = new TopK(poolSize);
        long scored = 0;
        var sync = new object();
        Parallel.ForEach(work, () => (Top: new TopK(poolSize), Scored: 0L), (item, _, local) =>
        {
            local.Scored += request.Exact
                ? ScanExact(item, query, local.Top)
                : ScanCodes(item, lut, querySum, local.Top);
            return local;
        }, local =>
        {
            lock (sync)
            {
                pool.Merge(local.Top);
                scored += local.Scored;
            }
        });

        var scanMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();

        // ---- stage 2: rescore the pool with int8 vectors
        var final = new TopK(request.Limit + DuplicateSlack);
        var rescored = 0;
        if (request.Exact)
        {
            final.Merge(pool);
        }
        else
        {
            var candidates = pool.ToDescending();
            rescored = candidates.Count;
            var rescoredScores = new float[candidates.Count];
            Parallel.For(0, candidates.Count, i => rescoredScores[i] = RescoreInt8(candidates[i].Candidate, query));
            for (var i = 0; i < candidates.Count; i++)
                final.Add(rescoredScores[i], candidates[i].Candidate);
        }

        var rescoreMs = phase.Elapsed.TotalMilliseconds;
        phase.Restart();

        // ---- the write buffer, scored exactly
        var memMatched = 0;
        var memTop = new TopK(request.Limit + DuplicateSlack);
        state.Active.Search(query, request.Filter, Definition.TimePath, memTop, ref memMatched);
        foreach (var frozen in state.Frozen)
            frozen.Search(query, request.Filter, Definition.TimePath, memTop, ref memMatched);

        var bufferMs = phase.Elapsed.TotalMilliseconds;

        // ---- resolve ids and de-duplicate before cutting to `limit`. An upsert can briefly be visible both
        // as the old segment row and the new buffer row; the buffer copy is the newer one and wins.
        var best = new Dictionary<string, (float Score, bool FromBuffer)>(StringComparer.Ordinal);
        foreach (var (candidate, score) in memTop.ToDescending())
            best.TryAdd(candidate.Mem!.Id, (score, true));
        foreach (var (candidate, score) in final.ToDescending())
        {
            var id = candidate.Segment!.GetId(candidate.Row);
            if (!best.TryGetValue(id, out var existing))
                best[id] = (score, false);
            else if (!existing.FromBuffer && score > existing.Score)
                best[id] = (score, false);
        }

        var hits = best
            .OrderByDescending(kv => kv.Value.Score)
            .Take(request.Limit)
            .Select(kv => new VectorSearchHit(kv.Key, (1.0 + kv.Value.Score) / 2.0))
            .ToList();

        return new VectorSearchResult(hits, new SearchStats
        {
            SegmentsTotal = segments.Count,
            SegmentsPrunedByTime = pruned,
            SegmentsFlat = flat,
            SegmentsIvf = ivf,
            RowsMatchedByFilter = matchedTotal,
            RowsScored = scored,
            RowsRescored = rescored,
            MemTableRowsMatched = memMatched,
            ElapsedMs = sw.Elapsed.TotalMilliseconds,
            FilterMs = filterMs,
            PlanMs = planMs,
            ScanMs = scanMs,
            RescoreMs = rescoreMs,
            BufferMs = bufferMs,
        });
    }

    /// <summary>
    /// Chooses the rows to scan. Exact queries, single-list segments and segments with at most
    /// <see cref="MadeleneIndexOptions.ExactScanThreshold"/> matching rows are scanned whole. The other segments
    /// share one IVF probe budget: lists are ranked by their centroid's similarity to the query across all of
    /// them and taken best-first until they hold <see cref="MadeleneIndexOptions.ProbeCandidateFactor"/> × pool
    /// matching rows and at least the list floor (<see cref="MadeleneIndexOptions.ProbeFraction"/> × mean lists
    /// per segment × √segments). The scan therefore grows with the pool and slowly with the segment count,
    /// instead of linearly with it. Centroid scoring runs per segment in parallel.
    /// </summary>
    private (List<WorkItem> Work, int Flat, int Ivf) Plan(VectorSearchRequest request, float[] query,
        List<(Segment Segment, Bitset? Mask, int Matched)> candidates, int poolSize)
    {
        var options = Definition.Options;
        var n = candidates.Count;
        var isFlat = new bool[n];
        for (var i = 0; i < n; i++)
        {
            var (seg, _, matched) = candidates[i];
            isFlat[i] = request.Exact || seg.Clusters == 1 || matched <= options.ExactScanThreshold;
        }

        // Similarity of the query to every centroid, and each IVF segment's lists in best-first order.
        var dots = new float[n][];
        var order = new int[n][];
        Parallel.For(0, n, i =>
        {
            var seg = candidates[i].Segment;
            var d = new float[seg.Clusters];
            for (var c = 0; c < seg.Clusters; c++)
                d[c] = VectorMath.Dot(query, seg.Centroids.AsSpan(c * seg.Dimensions, seg.Dimensions));
            dots[i] = d;
            if (isFlat[i])
                return;
            var keys = new float[seg.Clusters];
            var lists = new int[seg.Clusters];
            for (var c = 0; c < seg.Clusters; c++)
            {
                keys[c] = -d[c];
                lists[c] = c;
            }

            Array.Sort(keys, lists);
            order[i] = lists;
        });

        var work = new List<WorkItem>();
        int flat = 0, ivf = 0;
        long ivfLists = 0;
        for (var i = 0; i < n; i++)
        {
            if (isFlat[i])
            {
                flat++;
                var (seg, mask, _) = candidates[i];
                for (var c = 0; c < seg.Clusters; c++)
                    AddList(work, seg, mask, c, dots[i][c]);
            }
            else
            {
                ivf++;
                ivfLists += candidates[i].Segment.Clusters;
            }
        }

        if (ivf == 0)
            return (work, flat, ivf);

        var floor = (long)System.Math.Ceiling(options.ProbeFraction * ((double)ivfLists / ivf) * System.Math.Sqrt(ivf));
        var target = poolSize * options.ProbeCandidateFactor;
        var heap = new PriorityQueue<(int Segment, int Rank), float>(ivf);
        for (var i = 0; i < n; i++)
            if (!isFlat[i])
                heap.Enqueue((i, 0), -dots[i][order[i][0]]);

        double gathered = 0;
        long taken = 0;
        while (heap.TryDequeue(out var next, out _))
        {
            var (i, rank) = next;
            var (seg, mask, _) = candidates[i];
            var c = order[i][rank];
            int start = seg.ClusterOffsets[c], end = seg.ClusterOffsets[c + 1];
            gathered += mask == null ? end - start : mask.CountRange(start, end);
            taken++;
            AddList(work, seg, mask, c, dots[i][c]);
            if (rank + 1 < seg.Clusters)
                heap.Enqueue((i, rank + 1), -dots[i][order[i][rank + 1]]);
            if (gathered >= target && taken >= floor)
                break;
        }

        return (work, flat, ivf);
    }

    private static void AddList(List<WorkItem> work, Segment seg, Bitset? mask, int cluster, float centreDot)
    {
        for (var start = seg.ClusterOffsets[cluster]; start < seg.ClusterOffsets[cluster + 1]; start += MaxRangeRows)
        {
            var end = System.Math.Min(start + MaxRangeRows, seg.ClusterOffsets[cluster + 1]);
            work.Add(new WorkItem(seg, mask, start, end, centreDot));
        }
    }

    private static unsafe long ScanCodes(WorkItem item, float[] lut, float querySum, TopK top)
    {
        var seg = item.Segment;
        var codeBytes = seg.CodeBytes;
        var codes = seg.CodesPointer;
        var factors = seg.Factors;
        var mask = item.Mask;
        long scored = 0;
        fixed (float* table = lut)
        {
            for (var row = item.Start; row < item.End; row++)
            {
                if (mask != null && !mask.Get(row))
                    continue;
                scored++;
                var setSum = BinaryCodec.SumSetBits(codes + (long)row * codeBytes, codeBytes, table);
                var estimate = item.CentreDot + factors[row] * (2f * setSum - querySum);
                if (estimate > top.MinScore)
                    top.Add(estimate, new Candidate(seg, row, null));
            }
        }

        return scored;
    }

    private static unsafe long ScanExact(WorkItem item, float[] query, TopK top)
    {
        var seg = item.Segment;
        var mask = item.Mask;
        long scored = 0;
        for (var row = item.Start; row < item.End; row++)
        {
            if (mask != null && !mask.Get(row))
                continue;
            scored++;
            top.Add(RescoreInt8(new Candidate(seg, row, null), query), new Candidate(seg, row, null));
        }

        return scored;
    }

    private static unsafe float RescoreInt8(Candidate candidate, float[] query)
    {
        var seg = candidate.Segment!;
        var dims = seg.Dimensions;
        var vector = new ReadOnlySpan<sbyte>(seg.Vectors + (long)candidate.Row * dims, dims);
        return seg.Scales[candidate.Row] * VectorMath.DotInt8(query, vector);
    }

    private void ValidateFilterPaths(FilterNode node)
    {
        switch (node)
        {
            case AndNode and: foreach (var c in and.Children) ValidateFilterPaths(c); break;
            case OrNode or: foreach (var c in or.Children) ValidateFilterPaths(c); break;
            case NorNode nor: foreach (var c in nor.Children) ValidateFilterPaths(c); break;
            case NotNode not: ValidateFilterPaths(not.Child); break;
            case InNode @in when !Definition.IsFilterable(@in.Path):
            case RangeNode range when !Definition.IsFilterable(range.Path):
                var path = node is InNode i ? i.Path : ((RangeNode)node).Path;
                throw new FilterNotSupportedException($"Path '{path}' needs to be indexed as filter in index '{Definition.Name}'.");
        }
    }

    /// <summary>Conservative [lower, upper] time bounds implied by the filter, used only for segment pruning.</summary>
    internal static (long? Lower, long? Upper) TimeBounds(FilterNode node, string? timePath)
    {
        if (timePath == null)
            return (null, null);
        switch (node)
        {
            case RangeNode range when range.Path == timePath:
                return (range.Lower is { Kind: Model.FieldKind.Date } lo ? (long)lo.Number : null,
                        range.Upper is { Kind: Model.FieldKind.Date } hi ? (long)hi.Number : null);
            case InNode @in when @in.Path == timePath && @in.Values.Count > 0 && @in.Values.All(v => v.Kind == Model.FieldKind.Date):
                return ((long)@in.Values.Min(v => v.Number), (long)@in.Values.Max(v => v.Number));
            case AndNode and:
                long? lower = null, upper = null;
                foreach (var child in and.Children)
                {
                    var (l, u) = TimeBounds(child, timePath);
                    if (l != null)
                        lower = lower == null ? l : System.Math.Max(lower.Value, l.Value);
                    if (u != null)
                        upper = upper == null ? u : System.Math.Min(upper.Value, u.Value);
                }

                return (lower, upper);
            default:
                return (null, null);
        }
    }

    private sealed record WorkItem(Segment Segment, Bitset? Mask, int Start, int End, float CentreDot);
}
