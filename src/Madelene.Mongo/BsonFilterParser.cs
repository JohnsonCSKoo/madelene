using Madelene.Filtering;
using Madelene.Model;
using MongoDB.Bson;

namespace Madelene.Mongo;

/// <summary>Parses a $vectorSearch pre-filter written as a <see cref="BsonDocument"/> (same subset as the JSON parser).</summary>
public static class BsonFilterParser
{
    public static FilterNode Parse(BsonDocument filter)
    {
        var clauses = new List<FilterNode>();
        foreach (var element in filter)
        {
            switch (element.Name)
            {
                case "$and": clauses.Add(new AndNode(ParseArray(element))); break;
                case "$or": clauses.Add(new OrNode(ParseArray(element))); break;
                case "$nor": clauses.Add(new NorNode(ParseArray(element))); break;
                default:
                    if (element.Name.StartsWith('$'))
                        throw new FilterNotSupportedException($"Top-level operator '{element.Name}' is not supported in a vector search filter.");
                    clauses.Add(ParseField(element.Name, element.Value));
                    break;
            }
        }

        return clauses.Count switch
        {
            0 => FilterNode.MatchAll,
            1 => clauses[0],
            _ => new AndNode(clauses),
        };
    }

    private static List<FilterNode> ParseArray(BsonElement element)
    {
        if (element.Value is not BsonArray array || array.Count == 0)
            throw new VectorSearchRequestException($"{element.Name} must be a non-empty array.");
        return array.Select(v => v is BsonDocument d ? Parse(d) : throw new VectorSearchRequestException($"{element.Name} entries must be documents.")).ToList();
    }

    private static FilterNode ParseField(string path, BsonValue condition)
    {
        if (condition is BsonDocument ops && ops.ElementCount > 0 && ops.GetElement(0).Name.StartsWith('$'))
            return ParseOperators(path, ops);
        return new InNode(path, [Value(condition, path)]);
    }

    private static FilterNode ParseOperators(string path, BsonDocument ops)
    {
        var clauses = new List<FilterNode>();
        foreach (var op in ops)
        {
            clauses.Add(op.Name switch
            {
                "$eq" => new InNode(path, [Value(op.Value, path)]),
                "$ne" => new NotNode(new InNode(path, [Value(op.Value, path)])),
                "$in" => new InNode(path, Values(op, path)),
                "$nin" => new NotNode(new InNode(path, Values(op, path))),
                "$gt" => Range(path, op.Value, lower: true, inclusive: false),
                "$gte" => Range(path, op.Value, lower: true, inclusive: true),
                "$lt" => Range(path, op.Value, lower: false, inclusive: false),
                "$lte" => Range(path, op.Value, lower: false, inclusive: true),
                "$not" => op.Value is BsonDocument inner && inner.ElementCount > 0 && inner.GetElement(0).Name.StartsWith('$')
                    ? new NotNode(ParseOperators(path, inner))
                    : throw new VectorSearchRequestException("$not needs an operator document."),
                _ => throw new FilterNotSupportedException($"Operator '{op.Name}' on '{path}' is not supported in a vector search filter."),
            });
        }

        return clauses.Count == 1 ? clauses[0] : new AndNode(clauses);
    }

    private static RangeNode Range(string path, BsonValue raw, bool lower, bool inclusive)
    {
        var value = Value(raw, path);
        if (!value.IsOrdered)
            throw new FilterNotSupportedException($"Range operators on '{path}' need a number or date.");
        return lower ? new RangeNode(path, value, inclusive, null, false) : new RangeNode(path, null, false, value, inclusive);
    }

    private static List<FieldValue> Values(BsonElement op, string path) =>
        op.Value is BsonArray array
            ? array.Select(v => Value(v, path)).ToList()
            : throw new VectorSearchRequestException($"{op.Name} on '{path}' needs an array.");

    private static FieldValue Value(BsonValue value, string path)
    {
        if (value.IsBsonNull)
            throw new FilterNotSupportedException($"Null/missing equality on '{path}' is not supported yet.");
        if (value.IsBsonArray)
            throw new FilterNotSupportedException($"Whole-array equality on '{path}' is not supported; use $in.");
        return BsonFields.ToFieldValue(value)
               ?? throw new FilterNotSupportedException($"Value type {value.BsonType} on '{path}' is not supported.");
    }
}
