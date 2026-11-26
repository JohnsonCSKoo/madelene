using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Madelene.Numerics;

internal static class VectorMath
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = a.Length;
        var i = 0;
        var acc = Vector<float>.Zero;
        if (Vector.IsHardwareAccelerated && n >= Vector<float>.Count)
        {
            var va = MemoryMarshal.Cast<float, Vector<float>>(a);
            var vb = MemoryMarshal.Cast<float, Vector<float>>(b);
            for (var v = 0; v < va.Length; v++)
                acc += va[v] * vb[v];
            i = va.Length * Vector<float>.Count;
        }

        var sum = Vector.Sum(acc);
        for (; i < n; i++)
            sum += a[i] * b[i];
        return sum;
    }

    public static float Norm(ReadOnlySpan<float> a) => MathF.Sqrt(Dot(a, a));

    public static void NormalizeInPlace(Span<float> a)
    {
        var norm = Norm(a);
        if (norm <= 0 || float.IsNaN(norm))
            throw new ArgumentException("Cannot normalize a zero or NaN vector.");
        var inv = 1f / norm;
        for (var i = 0; i < a.Length; i++)
            a[i] *= inv;
    }

    /// <summary>Dot product of a float query with an int8 vector (no scale applied).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float DotInt8(ReadOnlySpan<float> q, ReadOnlySpan<sbyte> v)
    {
        var sum0 = 0f;
        var sum1 = 0f;
        var sum2 = 0f;
        var sum3 = 0f;
        var i = 0;
        for (; i + 4 <= q.Length; i += 4)
        {
            sum0 += q[i] * v[i];
            sum1 += q[i + 1] * v[i + 1];
            sum2 += q[i + 2] * v[i + 2];
            sum3 += q[i + 3] * v[i + 3];
        }

        for (; i < q.Length; i++)
            sum0 += q[i] * v[i];
        return sum0 + sum1 + sum2 + sum3;
    }

    /// <summary>y = M x for a row-major square matrix.</summary>
    public static void MatVec(ReadOnlySpan<float> matrix, ReadOnlySpan<float> x, Span<float> y)
    {
        var d = x.Length;
        for (var r = 0; r < d; r++)
            y[r] = Dot(matrix.Slice(r * d, d), x);
    }
}
