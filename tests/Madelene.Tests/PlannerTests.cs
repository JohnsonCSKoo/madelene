using Madelene.Filtering;
using Madelene.Model;
using Madelene.Search;
using Xunit;

namespace Madelene.Tests;

/// <summary>The shared IVF probe budget, the rescore pool cap and int8 merges.</summary>
public sealed class PlannerTests
{
    private const int Dims = 64;

    [Fact]
    public void IvfSegmentsShareOneProbeBudget()
    {
        using var tmp = new TempDirectory();
        const int days = 8, perDay = 10_000;
        var docs = TestData.Documents(days * perDay, Dims, seed: 11, days: days);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("p", Dims, new MadeleneIndexOptions
        {
            SealRows = 10_000, IvfMinRows = 5_000, ExactScanThreshold = 100, Bucket = TimeSpan.FromDays(1),
        }));
        index.UpsertMany(docs);
        index.Flush();
        index.Compact(force: true);
        Assert.Equal(days, index.Segments.Count(s => s.Clusters > 1));

        var rng = new Random(5);
        double recall = 0;
        long scored = 0;
        const int queries = 30, k = 10;
        for (var q = 0; q < queries; q++)
        {
            var query = TestData.Near(docs[rng.Next(docs.Count)].Vector, rng, 0.8f);
            var truth = docs.OrderByDescending(d => TestData.Dot(query, d.Vector)).Take(k).Select(d => d.Id).ToHashSet();
            var result = index.Search(Request(query, k, candidates: 100));
            Assert.Equal(days, result.Stats.SegmentsIvf);
            recall += (double)result.Hits.Count(h => truth.Contains(h.Id)) / k;
            scored += result.Stats.RowsScored;
        }

        // A per-segment budget of 8 × 1,000 matching rows would read ~80 % of every segment; the shared budget
        // reads ~8,000 rows across all eight.
        var scannedFraction = (double)scored / queries / docs.Count;
        Assert.True(scannedFraction < 0.25, $"scanned {scannedFraction:P1} of rows per query");
        Assert.True(recall / queries >= 0.90, $"recall {recall / queries:F3}");
    }

    [Fact]
    public void SelectiveFilterStillGathersEnoughMatchingRows()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(60_000, Dims, seed: 12, days: 6);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("s", Dims, new MadeleneIndexOptions
        {
            SealRows = 10_000, IvfMinRows = 5_000, ExactScanThreshold = 100, Bucket = TimeSpan.FromDays(1),
        }));
        index.UpsertMany(docs);
        index.Flush();
        index.Compact(force: true);

        const string filter = """{ "src": "src3" }"""; // ~5 % of rows: IVF probing, not the exact path
        var node = JsonFilterParser.Parse(filter);
        var rng = new Random(6);
        double recall = 0;
        const int queries = 30, k = 10;
        for (var q = 0; q < queries; q++)
        {
            var query = TestData.Near(docs[rng.Next(docs.Count)].Vector, rng, 0.8f);
            var truth = docs.Where(d => DocumentFilterEvaluator.Matches(node, d.Fields, "ts", d.TimeUnixMs))
                .OrderByDescending(d => TestData.Dot(query, d.Vector)).Take(k).Select(d => d.Id).ToHashSet();
            var result = index.Search(Request(query, k, candidates: 100, filter: filter));
            Assert.True(result.Stats.SegmentsIvf > 0);
            recall += (double)result.Hits.Count(h => truth.Contains(h.Id)) / k;
        }

        Assert.True(recall / queries >= 0.90, $"recall {recall / queries:F3}");
    }

    [Fact]
    public void LargeLimitsGetARescorePoolAboveTenThousand()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(20_000, Dims, seed: 13, days: 1);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("c", Dims, new MadeleneIndexOptions
        {
            SealRows = 50_000, IvfMinRows = 1_000_000, ExactScanThreshold = 100, RescorePoolFraction = 1.0,
        }));
        index.UpsertMany(docs);
        index.Flush();

        var query = docs[0].Vector;
        // pool = matched rows (fraction 1.0), capped at max(10,000, 64 × limit)
        Assert.Equal(10_000, index.Search(Request(query, 24, candidates: 2_000)).Stats.RowsRescored);
        Assert.Equal(200 * 64, index.Search(Request(query, 200, candidates: 2_000)).Stats.RowsRescored);
    }

    [Fact]
    public void Int8MergesKeepExactResults()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(12_000, Dims, seed: 14, days: 1);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("m", Dims, new MadeleneIndexOptions
        {
            SealRows = 3_000, IvfMinRows = 8_000, ExactScanThreshold = 100, Bucket = TimeSpan.FromDays(2),
        }));
        index.UpsertMany(docs);
        index.Flush();
        Assert.True(index.Segments.Count >= 4);

        var queries = Enumerable.Range(0, 10).Select(i => docs[i * 997].Vector).ToList();
        var before = queries.Select(q => index.Search(Request(q, 10, exact: true)).Hits).ToList();

        index.Compact(force: true); // rows are carried over as int8 and decoded per row by the builder
        Assert.Single(index.Segments);
        Assert.True(index.Segments[0].Clusters > 1);

        // Re-quantizing a decoded int8 vector can move a score by a rounding step, which may swap near-ties,
        // so compare as sets and per id.
        for (var i = 0; i < queries.Count; i++)
        {
            var after = index.Search(Request(queries[i], 10, exact: true)).Hits.ToDictionary(h => h.Id, h => h.Score);
            Assert.True(before[i].Count(h => after.ContainsKey(h.Id)) >= 9);
            Assert.Equal(before[i][0].Id, after.MaxBy(kv => kv.Value).Key);
            foreach (var hit in before[i].Where(h => after.ContainsKey(h.Id)))
                Assert.InRange(after[hit.Id] - hit.Score, -2e-3, 2e-3);
        }
    }

    private static VectorSearchRequest Request(float[] query, int limit, bool exact = false, string? filter = null, int? candidates = null) => new()
    {
        Index = "x",
        Path = "embedding",
        QueryVector = query,
        Limit = limit,
        NumCandidates = exact ? null : candidates ?? System.Math.Max(limit * 10, 100),
        Exact = exact,
        Filter = filter == null ? FilterNode.MatchAll : JsonFilterParser.Parse(filter),
    };
}
