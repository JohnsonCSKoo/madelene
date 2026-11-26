using Madelene.Filtering;
using Madelene.Model;
using Madelene.Mongo;
using Madelene.Storage;
using MongoDB.Bson;
using Xunit;

namespace Madelene.Tests;

/// <summary>
/// Property tests: the posting-list evaluator, the row evaluator and both parsers must agree
/// on randomly generated filters over randomly generated documents.
/// </summary>
public sealed class FilterSemanticsTests
{
    private const int Dims = 16;

    [Fact]
    public void SegmentEvaluatorMatchesReferenceOnRandomFilters()
    {
        using var tmp = new TempDirectory();
        var definition = TestData.Definition("f", Dims);
        var docs = TestData.Documents(3_000, Dims, seed: 11);
        using var index = VectorIndex.Create(tmp.Path, definition);
        index.UpsertMany(docs);
        index.Flush();

        var byId = docs.ToDictionary(d => d.Id);
        var rng = new Random(5);
        for (var trial = 0; trial < 300; trial++)
        {
            var filter = RandomFilter(rng, depth: 0);
            var expected = docs.Where(d => DocumentFilterEvaluator.Matches(filter, d.Fields, "ts", d.TimeUnixMs)).Select(d => d.Id).ToHashSet();

            var actual = new HashSet<string>();
            foreach (var seg in index.Segments)
            {
                var bits = new SegmentFilterEvaluator(seg, "ts").Evaluate(filter);
                for (var r = 0; r < seg.Rows; r++)
                    if (bits == null || bits.Get(r))
                        actual.Add(seg.GetId(r));
            }

            Assert.True(expected.SetEquals(actual), $"Mismatch for filter {Describe(filter)}: expected {expected.Count}, got {actual.Count}");
        }
    }

    [Theory]
    [InlineData("""{ "src": "src3" }""")]
    [InlineData("""{ "tags": { "$in": ["tag1", "tag2"] }, "flag": true }""")]
    [InlineData("""{ "$or": [ { "n": { "$gte": 10, "$lt": 20 } }, { "tags": { "$nin": ["tag4"] } } ] }""")]
    [InlineData("""{ "$nor": [ { "src": { "$ne": "src1" } } ] }""")]
    [InlineData("""{ "n": { "$not": { "$gt": 50 } }, "ts": { "$gte": { "$date": "2026-01-03T00:00:00Z" } } }""")]
    [InlineData("""{ "$and": [ { "src": { "$eq": "src2" } }, { "n": { "$lte": 70 } } ] }""")]
    public void JsonAndBsonParsersAgree(string filterJson)
    {
        var fromJson = JsonFilterParser.Parse(filterJson);
        var fromBson = BsonFilterParser.Parse(BsonDocument.Parse(filterJson));
        foreach (var doc in TestData.Documents(1_000, Dims, seed: 3))
        {
            Assert.Equal(
                DocumentFilterEvaluator.Matches(fromJson, doc.Fields, "ts", doc.TimeUnixMs),
                DocumentFilterEvaluator.Matches(fromBson, doc.Fields, "ts", doc.TimeUnixMs));
        }
    }

    [Theory]
    [InlineData("""{ "src": { "$exists": true } }""")]
    [InlineData("""{ "src": null }""")]
    [InlineData("""{ "tags": ["a", "b"] }""")]
    [InlineData("""{ "$text": { "$search": "x" } }""")]
    [InlineData("""{ "src": { "$regex": "^s" } }""")]
    public void UnsupportedFiltersThrowSoHostsCanFallBack(string filterJson)
    {
        Assert.Throws<FilterNotSupportedException>(() => JsonFilterParser.Parse(filterJson));
        Assert.Throws<FilterNotSupportedException>(() => BsonFilterParser.Parse(BsonDocument.Parse(filterJson)));
    }

    [Fact]
    public void NumbersOfDifferentWidthsCompareEqualAndTypesDoNotMix()
    {
        var fields = new Dictionary<string, IReadOnlyList<FieldValue>> { ["n"] = [FieldValue.Num(5)] };
        Assert.True(DocumentFilterEvaluator.Matches(BsonFilterParser.Parse(BsonDocument.Parse("""{ "n": { "$numberLong": "5" } }""")), fields, null, null));
        Assert.True(DocumentFilterEvaluator.Matches(BsonFilterParser.Parse(BsonDocument.Parse("""{ "n": 5.0 }""")), fields, null, null));
        Assert.False(DocumentFilterEvaluator.Matches(BsonFilterParser.Parse(BsonDocument.Parse("""{ "n": "5" }""")), fields, null, null));
        Assert.False(DocumentFilterEvaluator.Matches(JsonFilterParser.Parse("""{ "n": { "$gt": { "$date": "2020-01-01T00:00:00Z" } } }"""), fields, null, null));
    }

    [Fact]
    public void ArrayRangeBoundsMaySatisfyDifferentElementsLikeMongo()
    {
        // { a: [1, 10] } matches { a: { $gt: 5, $lt: 3 } } in MongoDB because 10 > 5 and 1 < 3.
        var fields = new Dictionary<string, IReadOnlyList<FieldValue>> { ["a"] = [FieldValue.Num(1), FieldValue.Num(10)] };
        Assert.True(DocumentFilterEvaluator.Matches(JsonFilterParser.Parse("""{ "a": { "$gt": 5, "$lt": 3 } }"""), fields, null, null));
    }

    private static FilterNode RandomFilter(Random rng, int depth)
    {
        var choice = depth >= 2 ? rng.Next(4) : rng.Next(8);
        return choice switch
        {
            0 => new InNode("src", Enumerable.Range(0, rng.Next(1, 4)).Select(_ => FieldValue.String("src" + rng.Next(22))).ToList()),
            1 => new InNode("tags", [FieldValue.String("tag" + rng.Next(32))]),
            2 => rng.Next(2) == 0
                ? new RangeNode("n", FieldValue.Num(rng.Next(100)), rng.Next(2) == 0, null, false)
                : new RangeNode("n", null, false, FieldValue.Num(rng.Next(100)), rng.Next(2) == 0),
            3 => rng.Next(3) switch
            {
                0 => new InNode("flag", [FieldValue.Bool(rng.Next(2) == 0)]),
                1 => new RangeNode("ts", FieldValue.Date(TestData.Epoch.AddDays(rng.NextDouble() * 10)), true, null, false),
                _ => new RangeNode("ts", null, false, FieldValue.Date(TestData.Epoch.AddDays(rng.NextDouble() * 10)), false),
            },
            4 => new AndNode([RandomFilter(rng, depth + 1), RandomFilter(rng, depth + 1)]),
            5 => new OrNode([RandomFilter(rng, depth + 1), RandomFilter(rng, depth + 1)]),
            6 => new NotNode(RandomFilter(rng, depth + 1)),
            _ => new NorNode([RandomFilter(rng, depth + 1), RandomFilter(rng, depth + 1)]),
        };
    }

    private static string Describe(FilterNode node) => node switch
    {
        AndNode a => "and(" + string.Join(",", a.Children.Select(Describe)) + ")",
        OrNode o => "or(" + string.Join(",", o.Children.Select(Describe)) + ")",
        NorNode n => "nor(" + string.Join(",", n.Children.Select(Describe)) + ")",
        NotNode n => "not(" + Describe(n.Child) + ")",
        InNode i => $"{i.Path} in [{string.Join(",", i.Values)}]",
        RangeNode r => $"{r.Path} {(r.Lower != null ? (r.LowerInclusive ? ">=" : ">") + r.Lower : "")}{(r.Upper != null ? (r.UpperInclusive ? "<=" : "<") + r.Upper : "")}",
        _ => node.ToString(),
    };
}
