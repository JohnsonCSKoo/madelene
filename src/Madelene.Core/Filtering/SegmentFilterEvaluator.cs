using System.Collections.Concurrent;
using Madelene.Model;
using Madelene.Storage;

namespace Madelene.Filtering;

/// <summary>Evaluates a filter against a segment's posting lists and time column, producing a row bitset.</summary>
internal sealed unsafe class SegmentFilterEvaluator
{
    private readonly Segment _segment;
    private readonly string? _timePath;
    private readonly ConcurrentDictionary<string, (double[] Values, int[] Terms)> _ordered;

    public SegmentFilterEvaluator(Segment segment, string? timePath)
    {
        _segment = segment;
        _timePath = timePath;
        _ordered = segment.OrderedTermCache;
    }

    /// <summary>Returns the matching rows, or null when everything matches.</summary>
    public Bitset? Evaluate(FilterNode node) => node is AllNode ? null : Eval(node);

    private Bitset Eval(FilterNode node)
    {
        var rows = _segment.Rows;
        switch (node)
        {
            case AllNode:
                return new Bitset(rows, fill: true);
            case AndNode and:
            {
                Bitset? acc = null;
                foreach (var child in and.Children)
                {
                    var bits = Eval(child);
                    if (acc == null)
                        acc = bits;
                    else
                        acc.And(bits);
                }

                return acc ?? new Bitset(rows, fill: true);
            }

            case OrNode or:
            {
                var acc = new Bitset(rows);
                foreach (var child in or.Children)
                    acc.Or(Eval(child));
                return acc;
            }

            case NorNode nor:
            {
                var acc = new Bitset(rows);
                foreach (var child in nor.Children)
                    acc.Or(Eval(child));
                acc.Not();
                return acc;
            }

            case NotNode not:
            {
                var bits = Eval(not.Child);
                bits.Not();
                return bits;
            }

            case InNode @in when @in.Path == _timePath:
            {
                var wanted = @in.Values.Where(v => v.Kind == FieldKind.Date).Select(v => (long)v.Number).ToHashSet();
                return TimeScan(t => wanted.Contains(t));
            }
            case InNode @in:
            {
                var acc = new Bitset(rows);
                foreach (var value in @in.Values)
                {
                    var term = _segment.TermIndex(TermKeys.Of(@in.Path, value));
                    if (term >= 0)
                        _segment.OrPostings(term, acc);
                }

                return acc;
            }

            case RangeNode range when range.Path == _timePath:
                return TimeRange(range);
            case RangeNode range:
                return RangeOverTerms(range);
            default:
                throw new FilterNotSupportedException($"Unknown filter node {node.GetType().Name}.");
        }
    }

    private Bitset TimeScan(Func<long, bool> predicate)
    {
        var bits = new Bitset(_segment.Rows);
        var times = _segment.Times;
        if (times == null)
            return bits;
        for (var r = 0; r < _segment.Rows; r++)
        {
            var t = times[r];
            if (t != long.MinValue && predicate(t))
                bits.Set(r);
        }

        return bits;
    }

    /// <summary>Tight loop over the time column: [lo, hi] in Unix ms, missing values never match.</summary>
    private Bitset TimeRange(RangeNode range)
    {
        var bits = new Bitset(_segment.Rows);
        var times = _segment.Times;
        var bound = range.Lower ?? range.Upper;
        if (times == null || bound is not { Kind: FieldKind.Date })
            return bits; // a number bound never matches a date (different type bracket)
        var lo = range.Lower is { } l ? (range.LowerInclusive ? (long)l.Number : (long)l.Number + 1) : long.MinValue + 1;
        var hi = range.Upper is { } h ? (range.UpperInclusive ? (long)h.Number : (long)h.Number - 1) : long.MaxValue;
        if (lo > _segment.Meta.MaxTime || hi < _segment.Meta.MinTime)
            return bits;
        var words = bits.Words;
        for (var w = 0; w < words.Length; w++)
        {
            ulong word = 0;
            var baseRow = w << 6;
            var end = System.Math.Min(64, _segment.Rows - baseRow);
            for (var b = 0; b < end; b++)
            {
                var t = times[baseRow + b];
                if (t >= lo && t <= hi)
                    word |= 1UL << b;
            }

            words[w] = word;
        }

        return bits;
    }

    private Bitset RangeOverTerms(RangeNode range)
    {
        var kind = (range.Lower ?? range.Upper)!.Value.Kind;
        var key = TermKeys.Prefix(range.Path, kind);
        var (values, terms) = _ordered.GetOrAdd(key, prefix =>
        {
            var (start, end) = _segment.TermPrefixRange(prefix);
            var vals = new double[end - start];
            var idx = new int[end - start];
            for (var t = start; t < end; t++)
            {
                var term = _segment.Terms[t];
                vals[t - start] = FieldValue.FromTerm(term[(range.Path.Length + 1)..]).Number;
                idx[t - start] = t;
            }

            Array.Sort(vals, idx);
            return (vals, idx);
        });

        var bits = new Bitset(_segment.Rows);
        for (var i = 0; i < values.Length; i++)
        {
            var value = kind == FieldKind.Date ? FieldValue.DateMs((long)values[i]) : FieldValue.Num(values[i]);
            if (range.Matches(value))
                _segment.OrPostings(terms[i], bits);
        }

        return bits;
    }
}
