using System.Runtime.CompilerServices;
using System.Text;

namespace Madelene.Storage;

/// <summary>Stable (cross-process) 64-bit hashing for ids. string.GetHashCode is randomized per process.</summary>
internal static class Hashing
{
    public static ulong Hash64(string value)
    {
        Span<byte> stack = stackalloc byte[256];
        var max = Encoding.UTF8.GetMaxByteCount(value.Length);
        var buffer = max <= stack.Length ? stack : new byte[max];
        var len = Encoding.UTF8.GetBytes(value, buffer);
        return Hash64(buffer[..len]);
    }

    public static ulong Hash64(ReadOnlySpan<byte> data)
    {
        // FNV-1a followed by a splitmix64 finalizer for avalanche.
        var h = 14695981039346656037UL;
        foreach (var b in data)
        {
            h ^= b;
            h *= 1099511628211UL;
        }

        return Mix(h);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Mix(ulong z)
    {
        z += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}

/// <summary>
/// Bloom filter over segment ids. Lets upserts of brand-new ids skip the per-segment id lookup,
/// so continuous inserts stay cheap no matter how many segments exist.
/// </summary>
internal static class Bloom
{
    public const int BitsPerKey = 10;
    public const int Hashes = 7;

    public static ulong[] Build(IEnumerable<ulong> hashes, int count)
    {
        var bits = System.Math.Max(64L, (long)count * BitsPerKey);
        var words = new ulong[(bits + 63) / 64];
        var m = (ulong)words.Length * 64;
        foreach (var h in hashes)
        {
            var h1 = h;
            var h2 = Hashing.Mix(h) | 1;
            for (var i = 0; i < Hashes; i++)
            {
                var bit = (h1 + (ulong)i * h2) % m;
                words[bit >> 6] |= 1UL << (int)(bit & 63);
            }
        }

        return words;
    }

    public static unsafe bool MayContain(ulong* words, long wordCount, ulong hash)
    {
        var m = (ulong)wordCount * 64;
        var h2 = Hashing.Mix(hash) | 1;
        for (var i = 0; i < Hashes; i++)
        {
            var bit = (hash + (ulong)i * h2) % m;
            if ((words[bit >> 6] & (1UL << (int)(bit & 63))) == 0)
                return false;
        }

        return true;
    }
}
