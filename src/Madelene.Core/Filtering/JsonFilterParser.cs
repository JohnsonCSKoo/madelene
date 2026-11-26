using System.Globalization;
using System.Text.Json;
using Madelene.Model;

namespace Madelene.Filtering;

/// <summary>
/// Parses a MongoDB $vectorSearch <c>filter</c> written as (relaxed or canonical) Extended JSON.
/// </summary>
public static class JsonFilterParser
{
    public static FilterNode Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement);
    }

    public static FilterNode Parse(JsonElement filter)
    {
        if (filter.ValueKind != JsonValueKind.Object)
            throw new VectorSearchRequestException("filter must be a document.");

        var clauses = new List<FilterNode>();
        foreach (var property in filter.EnumerateObject())
        {
            switch (property.Name)
            {
                case "$and":
                    clauses.Add(new AndNode(ParseArrayOfFilters(property)));
                    break;
                case "$or":
                    clauses.Add(new OrNode(ParseArrayOfFilters(property)));
                    break;
                case "$nor":
                    clauses.Add(new NorNode(ParseArrayOfFilters(property)));
                    break;
                default:
                    if (property.Name.StartsWith('$'))
                        throw new FilterNotSupportedException($"Top-level operator '{property.Name}' is not supported in a vector search filter.");
                    clauses.Add(ParseFieldCondition(property.Name, property.Value));
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

    private static List<FilterNode> ParseArrayOfFilters(JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Array || property.Value.GetArrayLength() == 0)
            throw new VectorSearchRequestException($"{property.Name} must be a non-empty array.");
        return property.Value.EnumerateArray().Select(Parse).ToList();
    }

    private static FilterNode ParseFieldCondition(string path, JsonElement condition)
    {
        if (condition.ValueKind == JsonValueKind.Object && IsOperatorDocument(condition))
            return ParseOperators(path, condition);
        return new InNode(path, [ParseValue(condition, path)]);
    }

    private static bool IsOperatorDocument(JsonElement element)
    {
        var first = element.EnumerateObject().FirstOrDefault();
        return first.Value.ValueKind != JsonValueKind.Undefined && first.Name.StartsWith('$') && !IsExtendedJsonValue(element);
    }

    private static FilterNode ParseOperators(string path, JsonElement operators)
    {
        var clauses = new List<FilterNode>();
        foreach (var op in operators.EnumerateObject())
        {
            clauses.Add(op.Name switch
            {
                "$eq" => new InNode(path, [ParseValue(op.Value, path)]),
                "$ne" => new NotNode(new InNode(path, [ParseValue(op.Value, path)])),
                "$in" => new InNode(path, ParseValueArray(op, path)),
                "$nin" => new NotNode(new InNode(path, ParseValueArray(op, path))),
                "$gt" => Range(path, op.Value, isLower: true, inclusive: false),
                "$gte" => Range(path, op.Value, isLower: true, inclusive: true),
                "$lt" => Range(path, op.Value, isLower: false, inclusive: false),
                "$lte" => Range(path, op.Value, isLower: false, inclusive: true),
                "$not" => op.Value.ValueKind == JsonValueKind.Object && IsOperatorDocument(op.Value)
                    ? new NotNode(ParseOperators(path, op.Value))
                    : throw new VectorSearchRequestException("$not needs an operator document."),
                _ => throw new FilterNotSupportedException($"Operator '{op.Name}' on '{path}' is not supported in a vector search filter."),
            });
        }

        return clauses.Count == 1 ? clauses[0] : new AndNode(clauses);
    }

    private static RangeNode Range(string path, JsonElement raw, bool isLower, bool inclusive)
    {
        var value = ParseValue(raw, path);
        if (!value.IsOrdered)
            throw new FilterNotSupportedException($"Range operators on '{path}' need a number or date, got {value.Kind}.");
        return isLower ? new RangeNode(path, value, inclusive, null, false) : new RangeNode(path, null, false, value, inclusive);
    }

    private static List<FieldValue> ParseValueArray(JsonProperty op, string path)
    {
        if (op.Value.ValueKind != JsonValueKind.Array)
            throw new VectorSearchRequestException($"{op.Name} on '{path}' needs an array.");
        return op.Value.EnumerateArray().Select(v => ParseValue(v, path)).ToList();
    }

    private static bool IsExtendedJsonValue(JsonElement element)
    {
        var names = element.EnumerateObject().Select(p => p.Name).ToList();
        return names.Count == 1 && names[0] is "$oid" or "$date" or "$numberLong" or "$numberInt" or "$numberDouble" or "$numberDecimal" or "$uuid";
    }

    internal static FieldValue ParseValue(JsonElement value, string path)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return FieldValue.String(value.GetString()!);
            case JsonValueKind.Number:
                return FieldValue.Num(value.GetDouble());
            case JsonValueKind.True:
                return FieldValue.Bool(true);
            case JsonValueKind.False:
                return FieldValue.Bool(false);
            case JsonValueKind.Object when IsExtendedJsonValue(value):
                var p = value.EnumerateObject().First();
                return p.Name switch
                {
                    "$oid" => FieldValue.ObjectId(p.Value.GetString()!),
                    "$date" => ParseDate(p.Value),
                    "$numberLong" or "$numberInt" or "$numberDouble" or "$numberDecimal" =>
                        FieldValue.Num(double.Parse(p.Value.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture)),
                    "$uuid" => FieldValue.Uuid(Guid.Parse(p.Value.GetString()!)),
                    _ => throw new FilterNotSupportedException($"Value type '{p.Name}' is not supported."),
                };
            case JsonValueKind.Null:
                throw new FilterNotSupportedException($"Null/missing equality on '{path}' is not supported yet.");
            case JsonValueKind.Array:
                throw new FilterNotSupportedException($"Whole-array equality on '{path}' is not supported; use $in.");
            default:
                throw new FilterNotSupportedException($"Value {value.ValueKind} on '{path}' is not supported.");
        }
    }

    private static FieldValue ParseDate(JsonElement date) => date.ValueKind switch
    {
        JsonValueKind.String => FieldValue.Date(DateTime.Parse(date.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal)),
        JsonValueKind.Number => FieldValue.Date(DateTime.UnixEpoch.AddMilliseconds(date.GetInt64())),
        JsonValueKind.Object when date.TryGetProperty("$numberLong", out var ms) =>
            FieldValue.Date(DateTime.UnixEpoch.AddMilliseconds(long.Parse(ms.GetString()!, CultureInfo.InvariantCulture))),
        _ => throw new VectorSearchRequestException("Malformed $date."),
    };
}
