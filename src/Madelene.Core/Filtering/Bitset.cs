using System.Numerics;
using System.Runtime.CompilerServices;

namespace Madelene.Filtering;

/// <summary>Dense, fixed-length bitset over segment row numbers.</summary>
public sealed class Bitset
{
    private readonly ulong[] _words;

    public Bitset(int length, bool fill = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        _words = new ulong[(length + 63) >> 6];
        if (fill)
            SetAll();
    }

    private Bitset(int length, ulong[] words)
    {
        Length = length;
        _words = words;
    }

    public int Length { get; }

    internal ulong[] Words => _words;

    public static Bitset FromWords(int length, ReadOnlySpan<ulong> words)
    {
        var bits = new Bitset(length);
        words[..bits._words.Length].CopyTo(bits._words);
        bits.ClearTail();
        return bits;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Get(int index) => (_words[index >> 6] & (1UL << index)) != 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(int index) => _words[index >> 6] |= 1UL << index;

    /// <summary>Thread-safe set, for tombstones written while readers scan.</summary>
    public bool SetAtomic(int index)
    {
        ref var word = ref _words[index >> 6];
        var mask = 1UL << index;
        var before = Interlocked.Or(ref word, mask);
        return (before & mask) == 0;
    }

    public void Clear(int index) => _words[index >> 6] &= ~(1UL << index);

    public void SetAll()
    {
        Array.Fill(_words, ulong.MaxValue);
        ClearTail();
    }

    public Bitset Clone() => new(Length, (ulong[])_words.Clone());

    public void And(Bitset other)
    {
        Check(other);
        for (var i = 0; i < _words.Length; i++)
            _words[i] &= other._words[i];
    }

    public void Or(Bitset other)
    {
        Check(other);
        for (var i = 0; i < _words.Length; i++)
            _words[i] |= other._words[i];
    }

    public void AndNot(Bitset other)
    {
        Check(other);
        for (var i = 0; i < _words.Length; i++)
            _words[i] &= ~other._words[i];
    }

    public void Not()
    {
        for (var i = 0; i < _words.Length; i++)
            _words[i] = ~_words[i];
        ClearTail();
    }

    public void OrWords(ReadOnlySpan<ulong> words)
    {
        for (var i = 0; i < _words.Length; i++)
            _words[i] |= words[i];
    }

    public int Count()
    {
        var count = 0;
        foreach (var w in _words)
            count += BitOperations.PopCount(w);
        return count;
    }

    /// <summary>Number of set bits in [start, end).</summary>
    public int CountRange(int start, int end)
    {
        var count = 0;
        for (var i = start; i < end;)
        {
            if ((i & 63) == 0 && i + 64 <= end)
            {
                count += BitOperations.PopCount(_words[i >> 6]);
                i += 64;
            }
            else
            {
                if (Get(i))
                    count++;
                i++;
            }
        }

        return count;
    }

    public IEnumerable<int> EnumerateSet()
    {
        for (var wi = 0; wi < _words.Length; wi++)
        {
            var w = _words[wi];
            while (w != 0)
            {
                var bit = BitOperations.TrailingZeroCount(w);
                yield return (wi << 6) + bit;
                w &= w - 1;
            }
        }
    }

    private void ClearTail()
    {
        var tail = Length & 63;
        if (tail != 0 && _words.Length > 0)
            _words[^1] &= (1UL << tail) - 1;
    }

    private void Check(Bitset other)
    {
        if (other.Length != Length)
            throw new ArgumentException($"Bitset length mismatch ({Length} vs {other.Length}).");
    }
}
