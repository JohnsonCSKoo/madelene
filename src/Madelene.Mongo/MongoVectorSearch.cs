using Madelene.Filtering;
using Madelene.Model;
using Madelene.Search;
using MongoDB.Bson;

namespace Madelene.Mongo;

/// <summary>
/// The pluggable seam: run a MongoDB <c>$vectorSearch</c> stage against Madelene and turn the hits back
/// into ordinary pipeline stages, so everything after the first stage stays unchanged.
/// </summary>
public static class MongoVectorSearch
{
    /// <summary>Field the rewritten pipeline stores the score in. <c>{$meta: "vectorSearchScore"}</c> is rewritten to it.</summary>
    public const string ScoreField = "__madeleneScore";

    public static VectorSearchRequest ParseStage(BsonDocument stage)
    {
        var body = stage.TryGetValue("$vectorSearch", out var inner) ? inner.AsBsonDocument : stage;
        string? index = null, path = null;
        float[]? vector = null;
        int? limit = null, candidates = null;
        var exact = false;
        var filter = FilterNode.MatchAll;
        foreach (var e in body)
        {
            switch (e.Name)
            {
                case "index": index = e.Value.AsString; break;
                case "path": path = e.Value.AsString; break;
                case "queryVector":
                    vector = e.Value switch
                    {
                        BsonArray a => a.Select(v => (float)v.ToDouble()).ToArray(),
                        BsonBinaryData { SubType: BsonBinarySubType.Vector } b => BsonFields.DecodeBinaryVector(b.Bytes),
                        _ => throw new VectorSearchRequestException("queryVector must be an array or a BSON vector."),
                    };
                    break;
                case "limit": limit = e.Value.ToInt32(); break;
                case "numCandidates": candidates = e.Value.ToInt32(); break;
                case "exact": exact = e.Value.ToBoolean(); break;
                case "filter": filter = BsonFilterParser.Parse(e.Value.AsBsonDocument); break;
                default: throw new VectorSearchRequestException($"Unknown $vectorSearch field '{e.Name}'.");
            }
        }

        var request = new VectorSearchRequest
        {
            Index = index ?? throw new VectorSearchRequestException("index is required."),
            Path = path ?? throw new VectorSearchRequestException("path is required."),
            QueryVector = vector ?? throw new VectorSearchRequestException("queryVector is required."),
            Limit = limit ?? throw new VectorSearchRequestException("limit is required."),
            NumCandidates = candidates,
            Exact = exact,
            Filter = filter,
        };
        request.Validate();
        return request;
    }

    /// <summary>Runs the stage and returns hits with their original BSON <c>_id</c>s, best first.</summary>
    public static IReadOnlyList<(BsonValue Id, double Score)> Search(MadeleneEngine engine, BsonDocument stage, out SearchStats stats)
    {
        var result = engine.Search(ParseStage(stage));
        stats = result.Stats;
        return result.Hits.Select(h => (IdCodec.Decode(h.Id), h.Score)).ToList();
    }

    /// <summary>
    /// Stages that turn hits back into documents in score order:
    /// <c>$match {_id: {$in}}</c>, <c>$addFields</c> with the score, <c>$sort</c>.
    /// </summary>
    public static BsonDocument[] HydrationStages(IReadOnlyList<(BsonValue Id, double Score)> hits, string scoreField = ScoreField)
    {
        var ids = new BsonArray(hits.Select(h => h.Id));
        var scores = new BsonArray(hits.Select(h => h.Score));
        return
        [
            new BsonDocument("$match", new BsonDocument("_id", new BsonDocument("$in", ids))),
            new BsonDocument("$addFields", new BsonDocument(scoreField,
                new BsonDocument("$arrayElemAt", new BsonArray
                {
                    scores,
                    new BsonDocument("$indexOfArray", new BsonArray { ids, "$_id" }),
                }))),
            new BsonDocument("$sort", new BsonDocument { { scoreField, -1 }, { "_id", 1 } }),
        ];
    }

    /// <summary>
    /// Rewrites a pipeline whose first stage is <c>$vectorSearch</c> so it runs on the plain collection.
    /// Any <c>{$meta: "vectorSearchScore"}</c> later in the pipeline is pointed at the injected score field.
    /// </summary>
    /// <exception cref="FilterNotSupportedException">The filter can't be served; run the original pipeline on MongoDB instead.</exception>
    public static BsonDocument[] RewritePipeline(MadeleneEngine engine, IReadOnlyList<BsonDocument> pipeline, out SearchStats stats)
    {
        if (pipeline.Count == 0 || !pipeline[0].Contains("$vectorSearch"))
            throw new ArgumentException("The first stage must be $vectorSearch.", nameof(pipeline));
        var hits = Search(engine, pipeline[0], out stats);
        var rest = pipeline.Skip(1).Select(stage => (BsonDocument)ReplaceScoreMeta(stage.DeepClone()));
        return [.. HydrationStages(hits), .. rest];
    }

    private static BsonValue ReplaceScoreMeta(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument doc when doc.ElementCount == 1 && doc.Contains("$meta") && doc["$meta"] == "vectorSearchScore":
                return "$" + ScoreField;
            case BsonDocument doc:
                foreach (var element in doc.ToList())
                    doc[element.Name] = ReplaceScoreMeta(element.Value);
                return doc;
            case BsonArray array:
                for (var i = 0; i < array.Count; i++)
                    array[i] = ReplaceScoreMeta(array[i]);
                return array;
            default:
                return value;
        }
    }
}

/// <summary>Maps MongoDB documents to <see cref="IndexDocument"/> for one index definition.</summary>
public sealed class BsonDocumentMapper
{
    private readonly VectorIndexDefinition _definition;

    public BsonDocumentMapper(VectorIndexDefinition definition) => _definition = definition;

    /// <summary>Fields to project when reading documents for this index.</summary>
    public IEnumerable<string> ProjectedPaths =>
        new[] { _definition.VectorPath }.Concat(_definition.FilterPaths).Concat(_definition.TimePath is { } t ? [t] : []).Distinct();

    /// <summary>Returns null when the document has no vector at the index path (it does not belong in the index).</summary>
    public IndexDocument? Map(BsonDocument document)
    {
        var vector = BsonFields.Vector(document, _definition.VectorPath);
        if (vector == null || vector.Length != _definition.Dimensions || !IsUsable(vector))
            return null; // a NaN or all-zero vector can't be indexed; skip it rather than stall the sync

        var fields = new Dictionary<string, IReadOnlyList<FieldValue>>(StringComparer.Ordinal);
        foreach (var path in _definition.FilterPaths)
        {
            var values = BsonFields.Values(document, path)
                .Select(BsonFields.ToFieldValue)
                .Where(v => v != null)
                .Select(v => v!.Value)
                .ToList();
            if (values.Count > 0)
                fields[path] = values;
        }

        long? time = null;
        if (_definition.TimePath != null)
        {
            var t = BsonFields.Values(document, _definition.TimePath).FirstOrDefault(v => v.BsonType == BsonType.DateTime);
            if (t != null)
                time = t.AsBsonDateTime.MillisecondsSinceEpoch;
        }

        return new IndexDocument(IdCodec.Encode(document["_id"]), vector, fields, time);
    }

    private bool IsUsable(float[] vector)
    {
        double sumSq = 0;
        foreach (var v in vector)
        {
            if (!float.IsFinite(v))
                return false;
            sumSq += (double)v * v;
        }

        return _definition.Similarity != VectorSimilarity.Cosine || sumSq > 0;
    }
}
