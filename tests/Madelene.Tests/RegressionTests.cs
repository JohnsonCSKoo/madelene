using Madelene.Filtering;
using Madelene.Model;
using Madelene.Mongo;
using Madelene.Search;
using Madelene.Storage;
using MongoDB.Bson;
using Xunit;

namespace Madelene.Tests;

/// <summary>Regression tests for issues found in review.</summary>
public sealed class RegressionTests
{
    private const int Dims = 16;

    [Fact]
    public void TimeEqualityAndInclusiveBoundsWorkOnSealedSegments()
    {
        using var tmp = new TempDirectory();
        using var index = VectorIndex.Create(tmp.Path, TestData.Definition("t", Dims));
        var stamp = new DateTime(2026, 3, 4, 5, 6, 7, 891, DateTimeKind.Utc);
        var docs = TestData.Documents(200, Dims, seed: 1);
        index.UpsertMany(docs);
        index.Upsert(new IndexDocument("exact", docs[0].Vector, docs[0].Fields, FieldValue.ToUnixMs(stamp)));
        index.Flush();

        foreach (var filter in new[]
                 {
                     $$"""{ "ts": { "$date": "{{stamp:O}}" } }""",
                     $$"""{ "ts": { "$in": [ { "$date": "{{stamp:O}}" } ] } }""",
                     $$"""{ "ts": { "$gte": { "$date": "{{stamp:O}}" } } }""", // bound == newest time in that segment
                     $$"""{ "ts": { "$lte": { "$date": "{{stamp:O}}" } } }""",
                 })
        {
            var hits = index.Search(new VectorSearchRequest
            {
                Index = "t", Path = "embedding", QueryVector = docs[0].Vector, Limit = 500, Exact = true,
                Filter = JsonFilterParser.Parse(filter),
            }).Hits;
            Assert.Contains(hits, h => h.Id == "exact");
        }
    }

    [Fact]
    public void UnixMillisecondConversionIsExactForMongoDates()
    {
        var stamp = new DateTime(2026, 11, 11, 23, 59, 59, 999, DateTimeKind.Utc);
        var ms = FieldValue.ToUnixMs(stamp);
        Assert.Equal(new DateTimeOffset(stamp).ToUnixTimeMilliseconds(), ms);
        Assert.Equal(stamp, FieldValue.FromUnixMs(ms));
        Assert.Equal((double)ms, FieldValue.Date(stamp).Number); // exactly representable
        Assert.Equal(-1, FieldValue.ToUnixMs(DateTime.UnixEpoch.AddTicks(-1))); // floors before 1970
    }

    [Fact]
    public void TruncatedOrMissingFilesFailCleanlyOnOpen()
    {
        using var tmp = new TempDirectory();
        var definition = TestData.Definition("c", Dims);
        using (var index = VectorIndex.Create(tmp.Path, definition))
        {
            index.UpsertMany(TestData.Documents(100, Dims, seed: 2, days: 1));
            index.Flush();
            index.Delete("doc5"); // now a segment tombstone, persisted by the next flush
            index.Flush();
        }

        var segDir = Directory.EnumerateDirectories(tmp.Path, "seg-*").First();
        var tomb = Directory.EnumerateFiles(segDir, "tomb-*").Single();
        var backup = Path.Combine(tmp.Path, "tomb.backup"); // outside the segment: open() garbage-collects stray tomb-* files
        File.Move(tomb, backup);
        Assert.Throws<InvalidDataException>(() => VectorIndex.Open(tmp.Path, "c"));
        File.Move(backup, tomb);

        var codes = Path.Combine(segDir, SegmentFileNames.Codes);
        using (var fs = new FileStream(codes, FileMode.Open))
            fs.SetLength(fs.Length - 1);
        Assert.Throws<InvalidDataException>(() => VectorIndex.Open(tmp.Path, "c"));
    }

    [Fact]
    public void MapperSkipsVectorsThatCannotBeIndexed()
    {
        var definition = new VectorIndexDefinition { Name = "m", VectorPath = "v", Dimensions = 3 };
        var mapper = new BsonDocumentMapper(definition);
        Assert.Null(mapper.Map(new BsonDocument { { "_id", 1 }, { "v", new BsonArray { 0.0, 0.0, 0.0 } } }));
        Assert.Null(mapper.Map(new BsonDocument { { "_id", 2 }, { "v", new BsonArray { double.NaN, 1.0, 0.0 } } }));
        Assert.NotNull(mapper.Map(new BsonDocument { { "_id", 3 }, { "v", new BsonArray { 1.0, 0.0, 0.0 } } }));
    }

    [Fact]
    public void DroppedIndexesDisappearAndLeftoversAreCleanedAtStartup()
    {
        using var tmp = new TempDirectory();
        using (var engine = new MadeleneEngine(tmp.Path))
        {
            engine.CreateIndex(TestData.Definition("a", Dims)).UpsertMany(TestData.Documents(50, Dims, seed: 3));
            engine.GetIndex("a").Flush();
            Assert.True(engine.DropIndex("a"));
            Assert.Empty(engine.IndexNames);
        }

        // Simulate a drop that was renamed but not deleted (crash or locked file).
        var leftover = Path.Combine(tmp.Path, "b.12345678~dropped");
        Directory.CreateDirectory(leftover);
        File.WriteAllText(Path.Combine(leftover, VectorIndex.DefinitionFile), "{}");
        using (var engine = new MadeleneEngine(tmp.Path))
            Assert.Empty(engine.IndexNames);
        Assert.False(Directory.Exists(leftover));
    }

    [Fact]
    public void CorruptIndexIsIsolatedAndCanBeDroppedAndRebuilt()
    {
        using var tmp = new TempDirectory();
        string brokenDir;
        using (var engine = new MadeleneEngine(tmp.Path))
        {
            foreach (var name in new[] { "good", "broken" })
            {
                var index = engine.CreateIndex(TestData.Definition(name, Dims));
                index.UpsertMany(TestData.Documents(100, Dims, seed: 5));
                index.Flush();
            }

            brokenDir = engine.GetIndex("broken").Directory;
        }

        var codes = Directory.EnumerateDirectories(brokenDir, "seg-*").Select(d => Path.Combine(d, SegmentFileNames.Codes)).First();
        using (var fs = new FileStream(codes, FileMode.Open))
            fs.SetLength(10);

        using (var engine = new MadeleneEngine(tmp.Path))
        {
            Assert.Equal(["good"], engine.IndexNames);
            Assert.IsType<InvalidDataException>(engine.FailedIndexes["broken"]);
            Assert.Throws<InvalidOperationException>(() => engine.CreateIndex(TestData.Definition("broken", Dims)));
            Assert.True(engine.DropIndex("broken"));
            engine.CreateIndex(TestData.Definition("broken", Dims)).UpsertMany(TestData.Documents(10, Dims, seed: 6));
        }

        Assert.False(Directory.Exists(brokenDir));
    }

    [Fact]
    public void IndexCanBeRecreatedWhileADroppedOneIsStillMapped()
    {
        using var tmp = new TempDirectory();
        using var engine = new MadeleneEngine(tmp.Path);
        var index = engine.CreateIndex(TestData.Definition("x", Dims));
        index.UpsertMany(TestData.Documents(100, Dims, seed: 7));
        index.Flush();
        var oldDir = index.Directory;
        var pinned = index.Segments[0];
        Assert.True(pinned.TryAddRef()); // like an in-flight query holding memory maps

        Assert.True(engine.DropIndex("x"));
        var recreated = engine.CreateIndex(TestData.Definition("x", Dims));
        Assert.NotEqual(oldDir, recreated.Directory);
        recreated.UpsertMany(TestData.Documents(10, Dims, seed: 8));
        recreated.Flush();
        pinned.Release();

        using var restarted = new MadeleneEngine(tmp.Path + Path.DirectorySeparatorChar);
        Assert.Contains("x", restarted.IndexNames);
        Assert.False(Directory.Exists(oldDir));
    }

    [Fact]
    public void UnusualBsonValuesMapWithoutThrowing()
    {
        Assert.Null(BsonFields.ToFieldValue(new BsonDecimal128(Decimal128.PositiveInfinity)));
        Assert.Equal(FieldValue.Num(1.5), BsonFields.ToFieldValue(new BsonDecimal128(1.5m)));
        var farFuture = new BsonDateTime(long.MaxValue / 2); // beyond DateTime.MaxValue
        Assert.Equal(FieldValue.DateMs(long.MaxValue / 2), BsonFields.ToFieldValue(farFuture));
    }

    [Fact]
    public void SearchOnDisposedIndexThrows()
    {
        using var tmp = new TempDirectory();
        var index = VectorIndex.Create(tmp.Path, TestData.Definition("d", Dims));
        index.UpsertMany(TestData.Documents(20, Dims, seed: 4));
        index.Flush();
        index.Dispose();
        Assert.Throws<ObjectDisposedException>(() => index.Search(new VectorSearchRequest
        {
            Index = "d", Path = "embedding", QueryVector = TestData.Documents(1, Dims, seed: 4)[0].Vector, Limit = 1, Exact = true,
        }));
    }
}
