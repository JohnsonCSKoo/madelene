using System.Runtime.CompilerServices;

namespace Madelene.Quantization;

/// <summary>
/// 1-bit RaBitQ-style codes.
/// <para>
/// For a stored vector o in a cluster with centre c, let r = P(o - c) (P a random rotation) and let the
/// code bit i be r_i &gt; 0. With q' = P q, MongoDB-style inner products are estimated as
/// &lt;o,q&gt; = &lt;c,q&gt; + &lt;r,q'&gt; and &lt;r,q'&gt; ≈ f · (2·S − Q), where S is the sum of q'_i over set
/// bits, Q is the sum of all q'_i, and f = |r|² / Σ|r_i| is stored per vector.
/// S is computed with one 256-entry lookup table per code byte.
/// </para>
/// </summary>
internal static class BinaryCodec
{
    public static int CodeBytes(int dimensions) => (dimensions + 7) / 8;

    /// <summary>Encodes an already rotated residual. Returns the correction factor f.</summary>
    public static float Encode(ReadOnlySpan<float> rotatedResidual, Span<byte> code)
    {
        code.Clear();
        double sumSq = 0;
        double sumAbs = 0;
        for (var i = 0; i < rotatedResidual.Length; i++)
        {
            var v = rotatedResidual[i];
            sumSq += v * v;
            sumAbs += System.Math.Abs(v);
            if (v > 0)
                code[i >> 3] |= (byte)(1 << (i & 7));
        }

        return sumAbs <= 0 ? 0f : (float)(sumSq / sumAbs);
    }

    /// <summary>Builds the per-byte lookup tables for a rotated query. Returns Q = Σ q'_i.</summary>
    public static float BuildLookup(ReadOnlySpan<float> rotatedQuery, float[] lut)
    {
        var codeBytes = CodeBytes(rotatedQuery.Length);
        if (lut.Length < codeBytes * 256)
            throw new ArgumentException("Lookup table too small.", nameof(lut));
        float total = 0;
        for (var j = 0; j < codeBytes; j++)
        {
            var baseIndex = j * 256;
            lut[baseIndex] = 0;
            for (var v = 1; v < 256; v++)
            {
                var low = System.Numerics.BitOperations.TrailingZeroCount(v);
                var dim = j * 8 + low;
                var qv = dim < rotatedQuery.Length ? rotatedQuery[dim] : 0f;
                lut[baseIndex + v] = lut[baseIndex + (v & (v - 1))] + qv;
            }
        }

        foreach (var v in rotatedQuery)
            total += v;
        return total;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe float SumSetBits(byte* code, int codeBytes, float* lut)
    {
        float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
        var j = 0;
        for (; j + 4 <= codeBytes; j += 4)
        {
            s0 += lut[(j << 8) + code[j]];
            s1 += lut[((j + 1) << 8) + code[j + 1]];
            s2 += lut[((j + 2) << 8) + code[j + 2]];
            s3 += lut[((j + 3) << 8) + code[j + 3]];
        }

        for (; j < codeBytes; j++)
            s0 += lut[(j << 8) + code[j]];
        return s0 + s1 + s2 + s3;
    }
}

/// <summary>Per-vector symmetric int8 scalar quantization used for rescoring.</summary>
internal static class Int8Codec
{
    public static float Encode(ReadOnlySpan<float> vector, Span<sbyte> output)
    {
        float maxAbs = 0;
        foreach (var v in vector)
            maxAbs = MathF.Max(maxAbs, MathF.Abs(v));
        var scale = maxAbs > 0 ? maxAbs / 127f : 1f;
        var inv = 1f / scale;
        for (var i = 0; i < vector.Length; i++)
            output[i] = (sbyte)System.Math.Clamp((int)MathF.Round(vector[i] * inv), -127, 127);
        return scale;
    }

    public static void Decode(ReadOnlySpan<sbyte> input, float scale, Span<float> output)
    {
        for (var i = 0; i < input.Length; i++)
            output[i] = input[i] * scale;
    }
}
