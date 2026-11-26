using Madelene.Model;

namespace Madelene.Filtering;

/// <summary>
/// Row-at-a-time evaluation against an in-memory document. This is the reference semantics; the
/// segment (posting list) evaluator must agree with it, which the tests check.
/// </summary>
public static class DocumentFilterEvaluator
{
    public static bool Matches(FilterNode node, IReadOnlyDictionary<string, IReadOnlyList<FieldValue>> fields, string? timePath, long? timeUnixMs)
    {
        switch (node)
        {
            case AllNode:
                return true;
            case AndNode and:
                foreach (var c in and.Children)
                    if (!Matches(c, fields, timePath, timeUnixMs))
                        return false;
                return true;
            case OrNode or:
                foreach (var c in or.Children)
                    if (Matches(c, fields, timePath, timeUnixMs))
                        return true;
                return false;
            case NorNode nor:
                foreach (var c in nor.Children)
                    if (Matches(c, fields, timePath, timeUnixMs))
                        return false;
                return true;
            case NotNode not:
                return !Matches(not.Child, fields, timePath, timeUnixMs);
            case InNode @in:
                foreach (var value in Values(@in.Path, fields, timePath, timeUnixMs))
                    foreach (var wanted in @in.Values)
                        if (value == wanted)
                            return true;
                return false;
            case RangeNode range:
                foreach (var value in Values(range.Path, fields, timePath, timeUnixMs))
                    if (range.Matches(value))
                        return true;
                return false;
            default:
                throw new FilterNotSupportedException($"Unknown filter node {node.GetType().Name}.");
        }
    }

    private static IEnumerable<FieldValue> Values(string path, IReadOnlyDictionary<string, IReadOnlyList<FieldValue>> fields, string? timePath, long? timeUnixMs)
    {
        if (timePath != null && path == timePath)
            return timeUnixMs is { } t ? [FieldValue.DateMs(t)] : [];
        return fields.TryGetValue(path, out var values) ? values : [];
    }
}
