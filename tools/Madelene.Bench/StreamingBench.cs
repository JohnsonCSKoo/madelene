using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Madelene.Filtering;
using Madelene.Model;
using Madelene.Numerics;
using Madelene.Search;

namespace Madelene.Bench;

/// <summary>
/// Large-scale benchmark that never holds the dataset in RAM: every row is regenerated deterministically
/// from (seed, row number). Rows are ingested in time order, like a continuous feed, and exact
/// ground truth is computed with one streaming pass over all rows.
/// </summary>
internal static class StreamingBench
{
    public static void Run(BenchOptions options, string dir)
    {
        var gen = new RowGenerator(options);
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))!);
        var needed = (long)options.Rows * 1_300;
        if (drive.AvailableFreeSpace < needed)
            throw new IOException($"Need ~{needed / 1e9:F0} GB free on {drive.Name}, have {drive.AvailableFreeSpace / 1e9:F0} GB.");

        var definition = new VectorIndexDefinition
        {
            Name = "bench",
            VectorPath = "embedding",
            Dimensions = options.Dimensions,
            FilterPaths = ["tenantId", "sourceId", "tags"],
            TimePath = "timestamp",
            Options = new MadeleneIndexOptions
            {
                SealRows = options.SealRows,
                IvfMinRows = options.IvfMinRows,
                Bucket = TimeSpan.FromDays(options.BucketDays),
                ProbeFraction = options.ProbeFraction,
                ExactScanThreshold = options.ExactScanThreshold,
            },
        };

        using var engine = new MadeleneEngine(dir);
        var log = new List<string>();
        void Log(string line)
        {
            var stamped = $"[{DateTime.UtcNow:HH:mm:ss}] {line}";
            Console.WriteLine(stamped);
            log.Add(stamped);
        }

        var total = Stopwatch.StartNew();
        double ingestSeconds = 0, compactSeconds = 0;
        VectorIndex index;
        if (options.Reuse && engine.TryGetIndex("bench", out var existing) && existing.Checkpoint == "bench"
            && existing.GetStatistics().LiveSegmentRows == options.Rows)
        {
            index = existing;
            Log($"reusing existing index: {index.GetStatistics()}");
        }
        else
        {
            if (engine.TryGetIndex("bench", out _) || engine.FailedIndexes.ContainsKey("bench"))
                engine.DropIndex("bench");
            index = engine.CreateIndex(definition);

            // ---- ingest in time order
            var sw = Stopwatch.StartNew();
            const int batch = 100_000;
            var docs = new IndexDocument[batch];
            for (long start = 0; start < options.Rows; start += batch)
            {
                var count = (int)System.Math.Min(batch, options.Rows - start);
                Parallel.For(0, count, i => docs[i] = gen.Document(start + i));
                for (var i = 0; i < count; i++)
                    index.Insert(docs[i]);
                if ((start + count) % 10_000_000 == 0)
                    Log($"ingested {start + count:N0} rows, {(start + count) / sw.Elapsed.TotalSeconds:N0} rows/s");
            }

            index.Flush("bench-ingested");
            ingestSeconds = sw.Elapsed.TotalSeconds;
            Log($"ingest done: {options.Rows / ingestSeconds:N0} rows/s ({ingestSeconds / 60:F1} min) {index.GetStatistics()}");

            sw.Restart();
            int replaced, rounds = 0;
            do
            {
                replaced = index.Compact();
                rounds++;
                Log($"compaction round {rounds}: replaced {replaced} segments, {sw.Elapsed.TotalMinutes:F1} min so far");
            }
            while (replaced > 0 && rounds < 4);

            index.Flush("bench"); // marks the index complete for --reuse
            compactSeconds = sw.Elapsed.TotalSeconds;
            Log($"compaction done in {compactSeconds / 60:F1} min");
        }

        LogMemory("after compaction", Log);
        var stats = index.GetStatistics();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var managed = GC.GetTotalMemory(forceFullCollection: true);
        Log($"index: {stats}");
        Log($"disk {stats.DiskBytes / 1e9:F1} GB = {(double)stats.DiskBytes / options.Rows:F0} B/vector; managed heap {managed / 1e9:F2} GB");

        // ---- ground truth (cached next to the index)
        var scenarios = Scenario.All(gen.Start, options.Days, options.Sources).ToArray();
        var queries = gen.Queries();
        var configs = options.QueryConfigs;
        var maxK = configs.Max(c => c.Limit);
        var truthWatch = Stopwatch.StartNew();
        var (truth, matchedCounts) = LoadOrComputeTruth(dir, gen, options, scenarios, queries, maxK);
        Log($"ground truth (top {maxK}): {truthWatch.Elapsed.TotalMinutes:F1} min");

        // ---- single-query latency: first touch, repeat, then the other shapes
        var results = new List<QueryResult>();
        void RunPass(string pass, QueryConfig config, bool exact, int s)
        {
            var latencies = new List<double>();
            double recall = 0, filter = 0, plan = 0, scan = 0, rescore = 0;
            long scoredRows = 0, rescoredRows = 0;
            var ivf = 0;
            for (var q = 0; q < queries.Length; q++)
            {
                var result = index.Search(new VectorSearchRequest
                {
                    Index = "bench", Path = "embedding", QueryVector = queries[q], Limit = config.Limit,
                    NumCandidates = exact ? null : config.Candidates, Exact = exact, Filter = scenarios[s].Filter,
                });
                var st = result.Stats;
                latencies.Add(st.ElapsedMs);
                filter += st.FilterMs;
                plan += st.PlanMs;
                scan += st.ScanMs;
                rescore += st.RescoreMs;
                scoredRows += st.RowsScored;
                rescoredRows += st.RowsRescored;
                ivf += st.SegmentsIvf;
                var expected = truth[s][q].Take(config.Limit).ToHashSet();
                recall += (double)result.Hits.Count(h => expected.Contains(long.Parse(h.Id, CultureInfo.InvariantCulture))) / expected.Count;
            }

            latencies.Sort();
            var n = queries.Length;
            var r = new QueryResult(scenarios[s].Name, exact ? $"exact limit {config.Limit}" : config.ToString(), pass,
                (double)matchedCounts[s] / options.Rows, recall / n, Pct(latencies, 0.5), Pct(latencies, 0.95), Pct(latencies, 0.99),
                (double)scoredRows / n, (double)rescoredRows / n, filter / n, plan / n, scan / n, rescore / n, (double)ivf / n);
            results.Add(r);
            Log(r.ToString());
        }

        var primary = configs[0];
        for (var s = 0; s < scenarios.Length; s++)
            RunPass("first", primary, exact: false, s);
        for (var s = 0; s < scenarios.Length; s++)
            RunPass("repeat", primary, exact: false, s);
        foreach (var config in configs.Skip(1))
            for (var s = 0; s < scenarios.Length; s++)
                RunPass("repeat", config, exact: false, s);
        for (var s = 0; s < scenarios.Length; s++)
            if (matchedCounts[s] <= options.ExactMaxRows)
                RunPass("repeat", primary, exact: true, s);

        // ---- throughput: concurrent clients issuing a mix of all scenarios
        var load = new List<LoadResult>();
        foreach (var clients in options.Clients)
        {
            var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
            var deadline = DateTime.UtcNow.AddSeconds(options.LoadSeconds);
            var threads = Enumerable.Range(0, clients).Select(c => new Thread(() =>
            {
                var rng = new Random(c);
                while (DateTime.UtcNow < deadline)
                {
                    var result = index.Search(new VectorSearchRequest
                    {
                        Index = "bench", Path = "embedding", QueryVector = queries[rng.Next(queries.Length)],
                        Limit = primary.Limit, NumCandidates = primary.Candidates, Filter = scenarios[rng.Next(scenarios.Length)].Filter,
                    });
                    latencies.Add(result.Stats.ElapsedMs);
                }
            })).ToList();
            var wall = Stopwatch.StartNew();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            var sorted = latencies.OrderBy(x => x).ToList();
            var l = new LoadResult(clients, sorted.Count / wall.Elapsed.TotalSeconds, Pct(sorted, 0.5), Pct(sorted, 0.95), Pct(sorted, 0.99));
            load.Add(l);
            Log(l.ToString());
        }

        LogMemory("after queries", Log);
        var process = Process.GetCurrentProcess();
        var report = new
        {
            options,
            ingestRowsPerSecond = ingestSeconds > 0 ? options.Rows / ingestSeconds : (double?)null,
            ingestMinutes = ingestSeconds / 60,
            compactMinutes = compactSeconds / 60,
            totalMinutes = total.Elapsed.TotalMinutes,
            index = stats,
            bytesPerVector = (double)stats.DiskBytes / options.Rows,
            managedHeapBytes = managed,
            workingSetBytes = process.WorkingSet64,
            results,
            load,
            log,
            machine = new { Environment.ProcessorCount, ramBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, os = Environment.OSVersion.ToString() },
        };
        var reportDir = Path.Combine(Environment.CurrentDirectory, "bench-results");
        Directory.CreateDirectory(reportDir);
        var reportPath = Path.Combine(reportDir, $"bench-stream-{options.Rows}x{options.Dimensions}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Log($"report: {reportPath}");

        if (!options.Keep)
            engine.DropIndex("bench");
    }

    /// <summary>Logs process memory: private bytes (what the OS can't reclaim), GC committed and GC heap.</summary>
    private static void LogMemory(string when, Action<string> log)
    {
        using var process = Process.GetCurrentProcess();
        var info = GC.GetGCMemoryInfo();
        log($"memory {when}: private {process.PrivateMemorySize64 / 1e9:F2} GB, working set {process.WorkingSet64 / 1e9:F2} GB, " +
            $"GC committed {info.TotalCommittedBytes / 1e9:F2} GB, GC heap {info.HeapSizeBytes / 1e9:F2} GB");
    }

    private static (long[][][] Truth, long[] Matched) LoadOrComputeTruth(string dir, RowGenerator gen, BenchOptions options, Scenario[] scenarios, float[][] queries, int k)
    {
        var path = Path.Combine(dir, $"truth-{options.Rows}-{options.Seed}-{options.Queries}-{k}-{options.Days}-{options.Sources}.json");
        if (File.Exists(path))
        {
            var cached = JsonSerializer.Deserialize<TruthFile>(File.ReadAllText(path))!;
            return (cached.Truth, cached.Matched);
        }

        var truth = GroundTruth(gen, options, scenarios, queries, k, out var matched);
        File.WriteAllText(path, JsonSerializer.Serialize(new TruthFile(truth, matched)));
        return (truth, matched);
    }

    private sealed record TruthFile(long[][][] Truth, long[] Matched);

    private static long[][][] GroundTruth(RowGenerator gen, BenchOptions options, Scenario[] scenarios, float[][] queries, int k, out long[] matched)
    {
        var global = scenarios.Select(_ => queries.Select(_ => new SmallTopK(k)).ToArray()).ToArray();
        var counts = new long[scenarios.Length];
        var sync = new object();
        Parallel.ForEach(
            System.Collections.Concurrent.Partitioner.Create(0L, options.Rows, 50_000),
            () => (Heaps: scenarios.Select(_ => queries.Select(_ => new SmallTopK(k)).ToArray()).ToArray(), Counts: new long[scenarios.Length], Scores: new float[queries.Length]),
            (range, _, local) =>
            {
                for (var row = range.Item1; row < range.Item2; row++)
                {
                    var doc = gen.Document(row);
                    var scored = false;
                    for (var s = 0; s < scenarios.Length; s++)
                    {
                        if (!DocumentFilterEvaluator.Matches(scenarios[s].Filter, doc.Fields, "timestamp", doc.TimeUnixMs))
                            continue;
                        if (!scored)
                        {
                            for (var q = 0; q < queries.Length; q++)
                                local.Scores[q] = VectorMath.Dot(queries[q], doc.Vector);
                            scored = true;
                        }

                        local.Counts[s]++;
                        for (var q = 0; q < queries.Length; q++)
                            local.Heaps[s][q].Add(local.Scores[q], row);
                    }
                }

                return local;
            },
            local =>
            {
                lock (sync)
                {
                    for (var s = 0; s < scenarios.Length; s++)
                    {
                        counts[s] += local.Counts[s];
                        for (var q = 0; q < queries.Length; q++)
                            global[s][q].Merge(local.Heaps[s][q]);
                    }
                }
            });
        matched = counts;
        return global.Select(perQuery => perQuery.Select(h => h.RowsBestFirst()).ToArray()).ToArray();
    }

    private static double Pct(List<double> sorted, double p) => sorted[System.Math.Clamp((int)System.Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

    private sealed class SmallTopK(int k)
    {
        private readonly PriorityQueue<long, float> _heap = new();

        public void Add(float score, long row)
        {
            if (_heap.Count < k)
                _heap.Enqueue(row, score);
            else if (_heap.TryPeek(out _, out var min) && score > min)
                _heap.DequeueEnqueue(row, score);
        }

        public void Merge(SmallTopK other)
        {
            foreach (var (row, score) in other._heap.UnorderedItems)
                Add(score, row);
        }

        public long[] RowsBestFirst() => _heap.UnorderedItems.OrderByDescending(x => x.Priority).Select(x => x.Element).ToArray();
    }
}

internal sealed record QueryResult(string Scenario, string Config, string Pass, double Selectivity, double Recall, double P50Ms, double P95Ms, double P99Ms,
    double AvgRowsScored, double AvgRowsRescored, double FilterMs, double PlanMs, double ScanMs, double RescoreMs, double IvfSegmentsPerQuery)
{
    public override string ToString() =>
        $"{Scenario,-32} {Config,-22} {Pass,-6} sel {Selectivity,8:P3} recall {Recall:F3} p50 {P50Ms,7:F1} p95 {P95Ms,7:F1} p99 {P99Ms,7:F1} ms | filter {FilterMs,6:F1} plan {PlanMs,5:F1} scan {ScanMs,6:F1} rescore {RescoreMs,6:F1} | scored {AvgRowsScored,11:N0} rescored {AvgRowsRescored,6:N0}";
}

internal sealed record LoadResult(int Clients, double Qps, double P50Ms, double P95Ms, double P99Ms)
{
    public override string ToString() => $"load: {Clients,2} clients  {Qps,7:F1} QPS  p50 {P50Ms,7:F1} ms  p95 {P95Ms,7:F1} ms  p99 {P99Ms,7:F1} ms";
}

/// <summary>Deterministic, allocation-light row generator (same distribution as <see cref="Dataset"/>).</summary>
internal sealed class RowGenerator
{
    private readonly BenchOptions _options;
    private readonly float[][] _centres;
    private readonly float[] _mean;
    private readonly float[] _gap;
    private readonly double[] _sourceCdf;
    private readonly double[] _tokenCdf;
    private readonly string[] _sourceNames;
    private readonly FieldValue[] _tokenValues;
    private readonly long _startMs;
    private readonly double _msPerRow;

    public RowGenerator(BenchOptions options)
    {
        _options = options;
        var rng = new SplitMix(options.Seed);
        _mean = Unit(Gaussian(ref rng, options.Dimensions));
        _gap = Unit(Gaussian(ref rng, options.Dimensions));
        _centres = Enumerable.Range(0, options.Clusters).Select(_ => Unit(Gaussian(ref rng, options.Dimensions))).ToArray();
        _sourceCdf = ZipfCdf(options.Sources, 1.1);
        _tokenCdf = ZipfCdf(options.Vocabulary, 1.0);
        _sourceNames = Enumerable.Range(0, options.Sources).Select(c => "src" + c).ToArray();
        _tokenValues = Enumerable.Range(0, options.Vocabulary).Select(t => FieldValue.String("tok" + t)).ToArray();
        _startMs = FieldValue.ToUnixMs(Start);
        _msPerRow = TimeSpan.FromDays(options.Days).TotalMilliseconds / options.Rows;
    }

    public DateTime Start { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public IndexDocument Document(long row)
    {
        var rng = new SplitMix(unchecked((ulong)_options.Seed * 0x9E3779B97F4A7C15UL + (ulong)row));
        var dims = _options.Dimensions;
        var centre = _centres[(int)(rng.NextULong() % (ulong)_centres.Length)];
        var v = new float[dims];
        var scale = 0.9f / MathF.Sqrt(dims);
        for (var d = 0; d < dims; d += 2)
        {
            var (g1, g2) = rng.GaussianPair();
            v[d] = centre[d] + 0.8f * _mean[d] + scale * (float)g1;
            if (d + 1 < dims)
                v[d + 1] = centre[d + 1] + 0.8f * _mean[d + 1] + scale * (float)g2;
        }

        Unit(v);
        var source = Sample(_sourceCdf, ref rng);
        var tokens = new HashSet<int>();
        while (tokens.Count < _options.TokensPerRow)
            tokens.Add(Sample(_tokenCdf, ref rng));
        var time = _startMs + (long)((row + rng.NextDouble()) * _msPerRow);
        return new IndexDocument(
            row.ToString(CultureInfo.InvariantCulture),
            v,
            new Dictionary<string, IReadOnlyList<FieldValue>>
            {
                ["tenantId"] = [FieldValue.String("t1")],
                ["sourceId"] = [FieldValue.String(_sourceNames[source])],
                ["tags"] = tokens.Select(t => _tokenValues[t]).ToArray(),
            },
            time);
    }

    public float[][] Queries()
    {
        var rng = new SplitMix(unchecked((ulong)_options.Seed ^ 0xABCDEF1234567UL));
        var dims = _options.Dimensions;
        return Enumerable.Range(0, _options.Queries).Select(_ =>
        {
            var centre = _centres[(int)(rng.NextULong() % (ulong)_centres.Length)];
            var g = Gaussian(ref rng, dims);
            var scale = 1.1f / MathF.Sqrt(dims);
            var q = new float[dims];
            for (var d = 0; d < dims; d++)
                q[d] = centre[d] + 0.8f * _mean[d] + scale * g[d] + (float)_options.ModalityGap * _gap[d];
            return Unit(q);
        }).ToArray();
    }

    private static int Sample(double[] cdf, ref SplitMix rng)
    {
        var idx = Array.BinarySearch(cdf, rng.NextDouble());
        return idx >= 0 ? idx : System.Math.Min(~idx, cdf.Length - 1);
    }

    private static double[] ZipfCdf(int n, double s)
    {
        var cdf = new double[n];
        double sum = 0;
        for (var i = 0; i < n; i++)
            cdf[i] = sum += 1.0 / System.Math.Pow(i + 1, s);
        for (var i = 0; i < n; i++)
            cdf[i] /= sum;
        return cdf;
    }

    private static float[] Gaussian(ref SplitMix rng, int dims)
    {
        var v = new float[dims];
        for (var d = 0; d < dims; d += 2)
        {
            var (a, b) = rng.GaussianPair();
            v[d] = (float)a;
            if (d + 1 < dims)
                v[d + 1] = (float)b;
        }

        return v;
    }

    private static float[] Unit(float[] v)
    {
        var n = VectorMath.Norm(v);
        for (var i = 0; i < v.Length; i++)
            v[i] /= n;
        return v;
    }

    private struct SplitMix(ulong seed)
    {
        private ulong _state = seed;

        public ulong NextULong()
        {
            var z = _state += 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }

        public SplitMix(int seed) : this((ulong)seed)
        {
        }

        public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

        public (double, double) GaussianPair()
        {
            var u1 = 1.0 - NextDouble();
            var u2 = NextDouble();
            var r = System.Math.Sqrt(-2 * System.Math.Log(u1));
            var (sin, cos) = System.Math.SinCos(2 * System.Math.PI * u2);
            return (r * cos, r * sin);
        }
    }
}
