using Madelene.Model;

namespace Madelene.Filtering;

/// <summary>
/// Filter AST for the MongoDB $vectorSearch pre-filter subset:
/// $and, $or, $nor, $not, $eq, $ne, $in, $nin, $gt, $gte, $lt, $lte.
/// </summary>
public abstract record FilterNode
{
    public static readonly FilterNode MatchAll = new AllNode();
}

/// <summary>Matches every document.</summary>
public sealed record AllNode : FilterNode;

public sealed record AndNode(IReadOnlyList<FilterNode> Children) : FilterNode;

public sealed record OrNode(IReadOnlyList<FilterNode> Children) : FilterNode;

/// <summary>Matches documents that match none of the children ($nor).</summary>
public sealed record NorNode(IReadOnlyList<FilterNode> Children) : FilterNode;

/// <summary>Logical complement. Used for $not, $ne and $nin (missing fields match, as in MongoDB).</summary>
public sealed record NotNode(FilterNode Child) : FilterNode;

/// <summary>Field equals any of the values. For array fields: any element equals any value.</summary>
public sealed record InNode(string Path, IReadOnlyList<FieldValue> Values) : FilterNode;

/// <summary>
/// Range comparison. Values of a different type bracket never match (MongoDB semantics).
/// The parsers emit one node per operator and AND them, so for array fields each bound may be satisfied
/// by a different element, exactly like MongoDB. A node with both bounds requires a single element to
/// satisfy both (used internally, e.g. for time-range pruning).
/// </summary>
public sealed record RangeNode(string Path, FieldValue? Lower, bool LowerInclusive, FieldValue? Upper, bool UpperInclusive) : FilterNode
{
    public bool Matches(FieldValue value)
    {
        var bound = Lower ?? Upper;
        if (bound is null || !value.IsOrdered || value.Kind != bound.Value.Kind)
            return false;
        if (Lower is { } lo && (LowerInclusive ? value.Number < lo.Number : value.Number <= lo.Number))
            return false;
        if (Upper is { } hi && (UpperInclusive ? value.Number > hi.Number : value.Number >= hi.Number))
            return false;
        return true;
    }
}
