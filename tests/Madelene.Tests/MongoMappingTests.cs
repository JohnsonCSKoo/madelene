using Madelene.Model;
using Madelene.Mongo;
using MongoDB.Bson;
using Xunit;

namespace Madelene.Tests;

public sealed class MongoMappingTests
{
    [Fact]
    public void IdsRoundTrip()
    {
        BsonValue[] ids = [ObjectId.GenerateNewId(), "abc", 42, 42L, new BsonDocument("a", 1), new BsonBinaryData(Guid.NewGuid(), GuidRepresentation.Standard)];
        foreach (var id in ids)
            Assert.Equal(id, IdCodec.Decode(IdCodec.Encode(id)));
    }

    [Fact]
    public void DottedPathsTraverseArraysOfDocuments()
    {
        var doc = BsonDocument.Parse("""
            { "_id": 1, "a": [ { "b": "x" }, { "b": ["y", "z"] }, { "c": 1 } ], "d": { "e": 5 } }
            """);
        Assert.Equal(["x", "y", "z"], BsonFields.Values(doc, "a.b").Select(v => v.AsString));
        Assert.Equal([5], BsonFields.Values(doc, "d.e").Select(v => v.AsInt32));
        Assert.Empty(BsonFields.Values(doc, "missing.path"));
    }

    [Fact]
    public void VectorsDecodeFromArraysAndBsonBinaryVectors()
    {
        var floats = new[] { 0.5f, -1.25f, 3f };
        var bytes = new byte[2 + floats.Length * 4];
        bytes[0] = 0x27;
        for (var i = 0; i < floats.Length; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(2 + i * 4), floats[i]);
        var doc = new BsonDocument
        {
            { "arr", new BsonArray(floats.Select(f => (double)f)) },
            { "bin", new BsonBinaryData(bytes, BsonBinarySubType.Vector) },
            { "int8", new BsonBinaryData([0x03, 0, 1, 0xFF, 127], BsonBinarySubType.Vector) },
            { "nested", new BsonDocument("v", new BsonArray { 1, 2, 3 }) },
        };
        Assert.Equal(floats, BsonFields.Vector(doc, "arr")!);
        Assert.Equal(floats, BsonFields.Vector(doc, "bin")!);
        Assert.Equal([1f, -1f, 127f], BsonFields.Vector(doc, "int8")!);
        Assert.Equal([1f, 2f, 3f], BsonFields.Vector(doc, "nested.v")!);
        Assert.Null(BsonFields.Vector(doc, "missing"));
    }

    [Fact]
    public void MapperExtractsDeclaredFieldsOnly()
    {
        var definition = new VectorIndexDefinition
        {
            Name = "m",
            VectorPath = "data.vector",
            Dimensions = 3,
            FilterPaths = ["tenantId", "tags", "meta.active", "_id"],
            TimePath = "timestamp",
        };
        var id = ObjectId.GenerateNewId();
        var doc = new BsonDocument
        {
            { "_id", id },
            { "tenantId", "t1" },
            { "tags", new BsonArray { "t1", "t2" } },
            { "meta", new BsonDocument("active", true) },
            { "timestamp", new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc) },
            { "data", new BsonDocument("vector", new BsonArray { 1.0, 0.0, 0.0 }) },
            { "Ignored", "x" },
        };

        var mapped = new BsonDocumentMapper(definition).Map(doc)!;
        Assert.Equal(IdCodec.Encode(id), mapped.Id);
        Assert.Equal([FieldValue.String("t1"), FieldValue.String("t2")], mapped.Fields["tags"]);
        Assert.Equal([FieldValue.Bool(true)], mapped.Fields["meta.active"]);
        Assert.Equal([FieldValue.ObjectId(id.ToString())], mapped.Fields["_id"]);
        Assert.False(mapped.Fields.ContainsKey("Ignored"));
        Assert.Equal(FieldValue.ToUnixMs(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc)), mapped.TimeUnixMs);

        doc["data"] = new BsonDocument();
        Assert.Null(new BsonDocumentMapper(definition).Map(doc)); // no vector: not part of the index
    }

    [Fact]
    public void RewritePipelineReplacesVectorSearchAndScoreMeta()
    {
        using var tmp = new TempDirectory();
        using var engine = new MadeleneEngine(tmp.Path);
        var index = engine.CreateIndex(new VectorIndexDefinition { Name = "i", VectorPath = "v", Dimensions = 8, FilterPaths = ["k"] });
        var a = ObjectId.GenerateNewId();
        var b = ObjectId.GenerateNewId();
        index.Upsert(new IndexDocument(IdCodec.Encode(a), [1, 0, 0, 0, 0, 0, 0, 0], new Dictionary<string, IReadOnlyList<FieldValue>> { ["k"] = [FieldValue.String("x")] }));
        index.Upsert(new IndexDocument(IdCodec.Encode(b), [0, 1, 0, 0, 0, 0, 0, 0], new Dictionary<string, IReadOnlyList<FieldValue>> { ["k"] = [FieldValue.String("y")] }));

        var pipeline = new[]
        {
            BsonDocument.Parse("""{ "$vectorSearch": { "index": "i", "path": "v", "queryVector": [1,0,0,0,0,0,0,0], "numCandidates": 10, "limit": 2, "filter": { "k": { "$in": ["x", "y"] } } } }"""),
            BsonDocument.Parse("""{ "$addFields": { "score": { "$meta": "vectorSearchScore" } } }"""),
            BsonDocument.Parse("""{ "$group": { "_id": null, "count": { "$sum": 1 } } }"""),
        };
        var rewritten = MongoVectorSearch.RewritePipeline(engine, pipeline, out var stats);

        Assert.Equal(5, rewritten.Length);
        var ids = rewritten[0]["$match"]["_id"]["$in"].AsBsonArray;
        Assert.Equal([a, b], ids.Select(v => v.AsObjectId));
        Assert.Equal("$" + MongoVectorSearch.ScoreField, rewritten[3]["$addFields"]["score"].AsString);
        Assert.Equal(2, stats.MemTableRowsMatched);
    }
}
