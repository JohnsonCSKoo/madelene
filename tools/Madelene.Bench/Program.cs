using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Madelene;
using Madelene.Bench;
using Madelene.Filtering;
using Madelene.Model;
using Madelene.Search;

// Madelene benchmark: builds an index from synthetic CLIP-like data (or real .npy embeddings),
// then measures recall@k against exact search and latency for several filter shapes.

var options = BenchOptions.Parse(args);
Console.WriteLine($"Madelene bench: {options}");
var dir = options.Directory ?? Path.Combine(Path.GetTempPath(), "madelene-bench", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
if (Directory.Exists(dir) && !options.Keep)
    Directory.Delete(dir, recursive: true);
Directory.CreateDirectory(dir);

if (options.Stream)
{
    StreamingBench.Run(options, dir);
    return;
}

var sw = Stopwatch.StartNew();
var data = Dataset.Create(options);
Console.WriteLine($"data: {data.Vectors.Length:N0} × {data.Dimensions} vectors, {data.Queries.Length} queries ({sw.Elapsed.TotalSeconds:F1}s)");

var definition = new VectorIndexDefinition
{
    Name = "bench",
    VectorPath = "embedding",
    Dimensions = data.Dimensions,
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
if (engine.TryGetIndex("bench", out _))
    engine.DropIndex("bench");
var index = engine.CreateIndex(definition);

sw.Restart();
for (var i = 0; i < data.Vectors.Length; i++)
    index.Upsert(data.Document(i));
index.Flush("bench");
var ingestSeconds = sw.Elapsed.TotalSeconds;
Console.WriteLine($"ingest: {data.Vectors.Length / ingestSeconds:N0} rows/s ({ingestSeconds:F1}s) {index.GetStatistics()}");

sw.Restart();
var compacted = index.Compact();
var compactSeconds = sw.Elapsed.TotalSeconds;
var stats = index.GetStatistics();
Console.WriteLine($"compaction: replaced {compacted} segments in {compactSeconds:F1}s {stats}");
Console.WriteLine($"disk: {stats.DiskBytes / 1e6:F1} MB = {(double)stats.DiskBytes / data.Vectors.Length:F0} B/vector");

var results = new List<ScenarioResult>();
foreach (var scenario in Scenario.All(data.Start, data.Days, data.Sources))
{
    var matching = data.Matching(scenario.Filter);
    if (matching.Length < options.K)
    {
        Console.WriteLine($"skip {scenario.Name}: only {matching.Length} matching rows");
        continue;
    }

    var truths = data.Queries.Select(q => data.ExactTopK(q, matching, options.K)).ToArray();
    foreach (var exact in new[] { false, true })
    {
        var latencies = new List<double>();
        double recallSum = 0;
        long scoredSum = 0;
        var ivf = 0;
        for (var q = 0; q < data.Queries.Length; q++)
        {
            var query = data.Queries[q];
            var truth = truths[q];
            var request = new VectorSearchRequest
            {
                Index = "bench",
                Path = "embedding",
                QueryVector = query,
                Limit = options.K,
                NumCandidates = exact ? null : options.Candidates,
                Exact = exact,
                Filter = scenario.Filter,
            };
            var result = index.Search(request);
            latencies.Add(result.Stats.ElapsedMs);
            scoredSum += result.Stats.RowsScored;
            ivf += result.Stats.SegmentsIvf;
            recallSum += (double)result.Hits.Count(h => truth.Contains(int.Parse(h.Id, CultureInfo.InvariantCulture))) / truth.Count;
        }

        latencies.Sort();
        var r = new ScenarioResult(
            scenario.Name + (exact ? " [exact]" : ""),
            Selectivity: (double)matching.Length / data.Vectors.Length,
            RecallAtK: recallSum / data.Queries.Length,
            P50Ms: Percentile(latencies, 0.50),
            P95Ms: Percentile(latencies, 0.95),
            P99Ms: Percentile(latencies, 0.99),
            AvgRowsScored: (double)scoredSum / data.Queries.Length,
            IvfSegmentsPerQuery: (double)ivf / data.Queries.Length);
        results.Add(r);
        Console.WriteLine(r);
    }
}

var report = new
{
    options,
    rows = data.Vectors.Length,
    dims = data.Dimensions,
    ingestRowsPerSecond = data.Vectors.Length / ingestSeconds,
    compactSeconds,
    index = stats,
    bytesPerVector = (double)stats.DiskBytes / data.Vectors.Length,
    results,
    machine = new { Environment.ProcessorCount, os = Environment.OSVersion.ToString() },
};
var reportDir = Path.Combine(Environment.CurrentDirectory, "bench-results");
Directory.CreateDirectory(reportDir);
var reportPath = Path.Combine(reportDir, $"bench-{data.Vectors.Length}x{data.Dimensions}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"report: {reportPath}");
if (!options.Keep)
{
    engine.Dispose();
    try
    {
        Directory.Delete(dir, recursive: true);
    }
    catch (IOException)
    {
        Console.WriteLine($"note: could not delete {dir}");
    }
}

static double Percentile(List<double> sorted, double p) => sorted[System.Math.Clamp((int)System.Math.Ceiling(p * sorted.Count) - 1, 0, sorted.Count - 1)];

namespace Madelene.Bench
{
    internal sealed record ScenarioResult(string Scenario, double Selectivity, double RecallAtK, double P50Ms, double P95Ms, double P99Ms, double AvgRowsScored, double IvfSegmentsPerQuery)
    {
        public override string ToString() =>
            $"{Scenario,-34} sel {Selectivity,8:P3}  recall {RecallAtK:F3}  p50 {P50Ms,8:F1} ms  p95 {P95Ms,8:F1} ms  p99 {P99Ms,8:F1} ms  scored {AvgRowsScored,12:N0}  ivf/q {IvfSegmentsPerQuery:F1}";
    }

    internal sealed record Scenario(string Name, FilterNode Filter)
    {
        public static IEnumerable<Scenario> All(DateTime start, int days, int sources)
        {
            var tenant = new InNode("tenantId", [FieldValue.String("t1")]);
            var end = start.AddDays(days);
            yield return new Scenario("tenant only", tenant);
            yield return new Scenario("tenant + busiest source", And(tenant, new InNode("sourceId", [FieldValue.String("src0")])));
            yield return new Scenario("tenant + mid source", And(tenant, new InNode("sourceId", [FieldValue.String("src" + sources / 4)])));
            yield return new Scenario("tenant + 2 tokens (AND)", And(tenant, new InNode("tags", [FieldValue.String("tok1")]), new InNode("tags", [FieldValue.String("tok7")])));
            yield return new Scenario("tenant + token OR + NOT", And(tenant, new OrNode([new InNode("tags", [FieldValue.String("tok3")]), new InNode("tags", [FieldValue.String("tok20")])]), new NotNode(new InNode("tags", [FieldValue.String("tok2")]))));
            yield return new Scenario("tenant + last 20% of time", And(tenant, new RangeNode("timestamp", FieldValue.Date(end.AddDays(-days * 0.2)), true, null, false)));
            yield return new Scenario("tenant + mid source + 5% of time", And(tenant,
                new InNode("sourceId", [FieldValue.String("src" + sources / 4)]),
                new RangeNode("timestamp", FieldValue.Date(start.AddDays(days * 0.5)), true, null, false),
                new RangeNode("timestamp", null, false, FieldValue.Date(start.AddDays(days * 0.55)), false)));
        }

        private static AndNode And(params FilterNode[] children) => new(children);
    }

    internal sealed record QueryConfig(int Limit, int Candidates)
    {
        public static QueryConfig Parse(string text)
        {
            var parts = text.Split(':');
            return new QueryConfig(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
        }

        public override string ToString() => $"limit {Limit}/cand {Candidates}";
    }

    internal sealed record BenchOptions
    {
        public int Rows { get; init; } = 200_000;
        public int Dimensions { get; init; } = 768;
        public int Queries { get; init; } = 100;
        public int K { get; init; } = 24;
        public int Candidates { get; init; } = 360;
        public int Days { get; init; } = 7;
        public double BucketDays { get; init; } = 1;
        public int Sources { get; init; } = 200;
        public int Vocabulary { get; init; } = 500;
        public int TokensPerRow { get; init; } = 18;
        public int Clusters { get; init; } = 1_000;
        public double ModalityGap { get; init; } = 0.5;
        public int SealRows { get; init; } = 250_000;
        public int IvfMinRows { get; init; } = 1_000_000;
        public double ProbeFraction { get; init; } = 0.03;
        public int ExactScanThreshold { get; init; } = 100_000;
        [System.Text.Json.Serialization.JsonIgnore] public string? Directory { get; init; }
        [System.Text.Json.Serialization.JsonIgnore] public string? VectorsNpy { get; init; }
        [System.Text.Json.Serialization.JsonIgnore] public string? QueriesNpy { get; init; }
        public bool Keep { get; init; }
        public int Seed { get; init; } = 7;

        /// <summary>Regenerate rows instead of holding them in RAM (needed beyond a few million vectors).</summary>
        public bool Stream { get; init; }

        /// <summary>Streaming mode runs exact search only for scenarios with at most this many matching rows.</summary>
        public long ExactMaxRows { get; init; } = 2_000_000;

        /// <summary>Reuse an existing, complete index in --dir instead of rebuilding it (use with --keep).</summary>
        public bool Reuse { get; init; }

        /// <summary>(limit, numCandidates) shapes to measure; the first one is also used for the load test.</summary>
        public IReadOnlyList<QueryConfig> QueryConfigs { get; init; } = [new(24, 360), new(360, 5400)];

        public IReadOnlyList<int> Clients { get; init; } = [1, 4, 16];

        public int LoadSeconds { get; init; } = 20;

        public static BenchOptions Parse(string[] args)
        {
            var o = new BenchOptions();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
                int Int() => int.Parse(Next(), CultureInfo.InvariantCulture);
                double Dbl() => double.Parse(Next(), CultureInfo.InvariantCulture);
                o = args[i] switch
                {
                    "--rows" => o with { Rows = Int() },
                    "--dims" => o with { Dimensions = Int() },
                    "--queries" => o with { Queries = Int() },
                    "--k" => o with { K = Int() },
                    "--candidates" => o with { Candidates = Int() },
                    "--days" => o with { Days = Int() },
                    "--bucket-days" => o with { BucketDays = Dbl() },
                    "--sources" => o with { Sources = Int() },
                    "--vocab" => o with { Vocabulary = Int() },
                    "--tokens" => o with { TokensPerRow = Int() },
                    "--clusters" => o with { Clusters = Int() },
                    "--modality-gap" => o with { ModalityGap = Dbl() },
                    "--seal-rows" => o with { SealRows = Int() },
                    "--ivf-min-rows" => o with { IvfMinRows = Int() },
                    "--probe-fraction" => o with { ProbeFraction = Dbl() },
                    "--exact-scan-threshold" => o with { ExactScanThreshold = Int() },
                    "--dir" => o with { Directory = Next() },
                    "--vectors" => o with { VectorsNpy = Next() },
                    "--query-vectors" => o with { QueriesNpy = Next() },
                    "--keep" => o with { Keep = true },
                    "--seed" => o with { Seed = Int() },
                    "--stream" => o with { Stream = true },
                    "--exact-max-rows" => o with { ExactMaxRows = Int() },
                    "--reuse" => o with { Reuse = true },
                    "--query-configs" => o with { QueryConfigs = Next().Split(',').Select(QueryConfig.Parse).ToList() },
                    "--clients" => o with { Clients = Next().Split(',').Select(x => int.Parse(x, CultureInfo.InvariantCulture)).ToList() },
                    "--load-seconds" => o with { LoadSeconds = Int() },
                    _ => throw new ArgumentException($"Unknown option {args[i]}"),
                };
            }

            return o;
        }
    }
}
