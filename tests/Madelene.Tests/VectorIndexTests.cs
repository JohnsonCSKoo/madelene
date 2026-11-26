using Madelene.Filtering;
using Madelene.Model;
using Madelene.Search;
using Xunit;

namespace Madelene.Tests;

public sealed class VectorIndexTests
{
    private const int Dims = 64;

    [Fact]
    public void ApproximateSearchRecallIsHighOnFlatAndIvfSegments()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(20_000, Dims, seed: 1, days: 1);
        // One bucket (1 day), so a forced compaction merges everything into one IVF segment.
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("r", Dims, new MadeleneIndexOptions
        {
            SealRows = 5_000, IvfMinRows = 8_000, ExactScanThreshold = 100, Bucket = TimeSpan.FromDays(2),
        }));
        index.UpsertMany(docs);
        index.Flush();

        var flatRecall = MeanRecall(index, docs, filter: null);
        Assert.True(flatRecall >= 0.95, $"flat recall {flatRecall:F3}");

        index.Compact(force: true);
        Assert.Contains(index.Segments, s => s.Clusters > 1);
        var ivfRecall = MeanRecall(index, docs, filter: null);
        Assert.True(ivfRecall >= 0.90, $"ivf recall {ivfRecall:F3}");

        var filtered = MeanRecall(index, docs, filter: """{ "src": { "$in": ["src1", "src2"] }, "flag": true }""");
        Assert.True(filtered >= 0.90, $"filtered recall {filtered:F3}");
    }

    [Fact]
    public void ExactSearchMatchesBruteForceOrder()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(3_000, Dims, seed: 2);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("e", Dims));
        index.UpsertMany(docs);
        index.Flush();

        var query = docs[17].Vector;
        var result = index.Search(Request(query, limit: 10, exact: true));
        var truth = docs.OrderByDescending(d => TestData.Dot(query, d.Vector)).Take(10).Select(d => d.Id).ToList();
        Assert.True(result.Hits.Select(h => h.Id).Intersect(truth).Count() >= 9);
        Assert.Equal("doc17", result.Hits[0].Id);
        Assert.InRange(result.Hits[0].Score, 0.99, 1.0); // (1 + cos) / 2 with cos ≈ 1
    }

    [Fact]
    public void UpsertReplacesAndDeleteRemovesAcrossBufferAndSegments()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(500, Dims, seed: 3);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("u", Dims));
        index.UpsertMany(docs);
        index.Flush(); // all in segments now

        var target = docs[0];
        var other = docs[1];
        // Move doc0's vector onto doc1's vector: searching for doc1's vector must now return both ids, once each.
        index.Upsert(new IndexDocument(target.Id, other.Vector, target.Fields, target.TimeUnixMs));
        var hits = index.Search(Request(other.Vector, limit: 5, exact: true)).Hits;
        Assert.Single(hits, h => h.Id == target.Id);
        Assert.Contains(hits.Take(2), h => h.Id == target.Id);

        Assert.True(index.Delete(target.Id));
        Assert.True(index.Delete(other.Id));
        Assert.False(index.Delete(other.Id));
        hits = index.Search(Request(other.Vector, limit: 50, exact: true)).Hits;
        Assert.DoesNotContain(hits, h => h.Id == target.Id || h.Id == other.Id);

        // The same, after another flush and a compaction.
        index.Flush();
        index.Compact(force: true);
        hits = index.Search(Request(other.Vector, limit: 50, exact: true)).Hits;
        Assert.DoesNotContain(hits, h => h.Id == target.Id || h.Id == other.Id);
        Assert.Equal(498, index.GetStatistics().LiveSegmentRows);
    }

    [Fact]
    public void FlushedStateAndCheckpointSurviveReopen()
    {
        using var tmp = new TempDirectory();
        var definition = TestData.Definition("p", Dims);
        var docs = TestData.Documents(4_500, Dims, seed: 4);
        var query = docs[42].Vector;
        IReadOnlyList<VectorSearchHit> before;
        using (var index = VectorIndex.Create(tmp.Path, definition))
        {
            index.UpsertMany(docs);
            index.Delete("doc42");
            index.Flush("token-1");
            index.Upsert(new IndexDocument("unflushed", query));
            before = index.Search(Request(query, 20)).Hits.Where(h => h.Id != "unflushed").ToList();
        }

        using (var reopened = VectorIndex.Open(tmp.Path, "p"))
        {
            Assert.Equal("token-1", reopened.Checkpoint);
            var after = reopened.Search(Request(query, 20)).Hits;
            Assert.DoesNotContain(after, h => h.Id == "doc42");
            Assert.DoesNotContain(after, h => h.Id == "unflushed"); // not durable until the next flush; the host replays it
            Assert.Equal(before.Take(10).Select(h => h.Id), after.Take(10).Select(h => h.Id));
        }
    }

    [Fact]
    public void RetentionDropsWholeSegmentsAndTrimsPartialOnes()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(6_000, Dims, seed: 5, days: 10);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("t", Dims));
        index.UpsertMany(docs);
        index.Flush();
        var segmentsBefore = index.GetStatistics().Segments;

        var cutoff = TestData.Epoch.AddDays(5.5);
        var removed = index.DeleteOlderThan(cutoff);
        var expectedRemoved = docs.Count(d => d.TimeUnixMs < FieldValue.ToUnixMs(cutoff));
        Assert.Equal(expectedRemoved, removed);
        Assert.True(index.GetStatistics().Segments < segmentsBefore);

        var hits = index.Search(Request(docs[0].Vector, limit: 200, exact: true)).Hits;
        var byId = docs.ToDictionary(d => d.Id);
        Assert.All(hits, h => Assert.True(byId[h.Id].TimeUnixMs >= FieldValue.ToUnixMs(cutoff)));
    }

    [Fact]
    public void TimeFiltersPruneSegmentsAndStayCorrect()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(6_000, Dims, seed: 6, days: 10);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("tp", Dims));
        index.UpsertMany(docs);
        index.Flush();

        var from = TestData.Epoch.AddDays(2);
        var to = TestData.Epoch.AddDays(3);
        var filter = $$"""{ "ts": { "$gte": { "$date": "{{from:O}}" }, "$lt": { "$date": "{{to:O}}" } } }""";
        var result = index.Search(Request(docs[0].Vector, limit: 100, exact: true, filter: filter));
        Assert.True(result.Stats.SegmentsPrunedByTime > 0);
        var expected = docs.Where(d => d.TimeUnixMs >= FieldValue.ToUnixMs(from) && d.TimeUnixMs < FieldValue.ToUnixMs(to)).OrderByDescending(d => TestData.Dot(docs[0].Vector, d.Vector)).Take(100).Select(d => d.Id).ToHashSet();
        Assert.True(result.Hits.Count(h => expected.Contains(h.Id)) >= 95);
        Assert.All(result.Hits, h => Assert.Contains(h.Id, expected.Concat(docs.Where(d => d.TimeUnixMs >= FieldValue.ToUnixMs(from) && d.TimeUnixMs < FieldValue.ToUnixMs(to)).Select(d => d.Id))));
    }

    [Fact]
    public void RequestValidationMirrorsMongo()
    {
        using var tmp = new TempDirectory();
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("v", Dims));
        var q = new float[Dims];
        q[0] = 1;
        Assert.Throws<VectorSearchRequestException>(() => index.Search(new VectorSearchRequest { Index = "v", Path = "embedding", QueryVector = q, Limit = 10, NumCandidates = 5 }));
        Assert.Throws<VectorSearchRequestException>(() => index.Search(new VectorSearchRequest { Index = "v", Path = "embedding", QueryVector = q, Limit = 10 }));
        Assert.Throws<VectorSearchRequestException>(() => index.Search(new VectorSearchRequest { Index = "v", Path = "embedding", QueryVector = q, Limit = 10, NumCandidates = 20_000 }));
        Assert.Throws<VectorSearchRequestException>(() => index.Search(new VectorSearchRequest { Index = "v", Path = "embedding", QueryVector = q, Limit = 10, NumCandidates = 10, Exact = true }));
        Assert.Throws<VectorSearchRequestException>(() => index.Search(new VectorSearchRequest { Index = "v", Path = "other", QueryVector = q, Limit = 10, NumCandidates = 10 }));
        Assert.Throws<FilterNotSupportedException>(() => index.Search(Request(q, 10, filter: """{ "notIndexed": 1 }""")));
    }

    [Fact]
    public void EngineRoutesSearchJsonByIndexName()
    {
        using var tmp = new TempDirectory();
        using (var engine = new MadeleneEngine(tmp.Path))
        {
            var index = engine.CreateIndex(TestData.Definition("a.b.c/x", Dims));
            var docs = TestData.Documents(300, Dims, seed: 7);
            index.UpsertMany(docs);
            index.Flush();
            var json = $$"""
                { "$vectorSearch": { "index": "a.b.c/x", "path": "embedding",
                  "queryVector": [{{string.Join(",", docs[3].Vector.Select(v => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture)))}}],
                  "numCandidates": 50, "limit": 5, "filter": { "src": { "$in": ["{{docs[3].Fields["src"][0].Text}}"] } } } }
                """;
            Assert.Equal("doc3", engine.Search(json).Hits[0].Id);
        }

        using (var reopened = new MadeleneEngine(tmp.Path))
            Assert.Contains("a.b.c/x", reopened.IndexNames);
    }

    [Fact]
    public async Task ConcurrentSearchesDuringIngestFlushAndCompaction()
    {
        using var tmp = new TempDirectory();
        var docs = TestData.Documents(12_000, Dims, seed: 8, days: 2);
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("c", Dims, new MadeleneIndexOptions { SealRows = 1_000, IvfMinRows = 3_000, MergeFactor = 3 }));
        using var cts = new CancellationTokenSource();
        var errors = new List<Exception>();
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            var rng = new Random(r);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    var hits = index.Search(Request(docs[rng.Next(docs.Count)].Vector, 10)).Hits;
                    if (hits.Select(h => h.Id).Distinct().Count() != hits.Count)
                        throw new InvalidOperationException("duplicate ids in results");
                }
                catch (Exception ex)
                {
                    lock (errors)
                        errors.Add(ex);
                }
            }
        })).ToArray();

        for (var i = 0; i < docs.Count; i++)
        {
            index.Upsert(docs[i]);
            if (i % 997 == 0)
                index.Delete(docs[i / 2].Id);
            if (i % 4_000 == 3_999)
                index.Compact();
        }

        index.Flush();
        index.Compact(force: true);
        await cts.CancelAsync();
        await Task.WhenAll(readers);
        Assert.Empty(errors);
    }

    private static double MeanRecall(VectorIndex index, List<IndexDocument> docs, string? filter, int queries = 40, int k = 10)
    {
        var rng = new Random(99);
        var node = filter == null ? FilterNode.MatchAll : JsonFilterParser.Parse(filter);
        double total = 0;
        for (var q = 0; q < queries; q++)
        {
            var query = TestData.Near(docs[rng.Next(docs.Count)].Vector, rng, 0.8f);
            var truth = docs.Where(d => DocumentFilterEvaluator.Matches(node, d.Fields, "ts", d.TimeUnixMs))
                .OrderByDescending(d => TestData.Dot(query, d.Vector)).Take(k).Select(d => d.Id).ToHashSet();
            var hits = index.Search(Request(query, k, filter: filter, candidates: 200)).Hits;
            total += (double)hits.Count(h => truth.Contains(h.Id)) / truth.Count;
        }

        return total / queries;
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
