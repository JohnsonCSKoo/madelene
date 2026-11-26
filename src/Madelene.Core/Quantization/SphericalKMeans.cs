using Madelene.Numerics;

namespace Madelene.Quantization;

/// <summary>Spherical k-means (cosine) used to build the IVF layer of large segments.</summary>
internal static class SphericalKMeans
{
    public static float[][] Train(IReadOnlyList<float[]> points, int k, int iterations, int maxSample, int seed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(k, 1);
        var rng = new Random(seed);
        var dims = points[0].Length;

        var sample = points.Count <= maxSample
            ? points.ToArray()
            : Enumerable.Range(0, maxSample).Select(_ => points[rng.Next(points.Count)]).ToArray();
        k = System.Math.Min(k, sample.Length);

        var centroids = new float[k][];
        var order = Enumerable.Range(0, sample.Length).OrderBy(_ => rng.Next()).ToArray();
        for (var c = 0; c < k; c++)
            centroids[c] = Normalized(sample[order[c]]);

        var labels = new int[sample.Length];
        for (var iter = 0; iter < iterations; iter++)
        {
            AssignInto(sample, centroids, labels);

            var sums = new double[k][];
            var counts = new int[k];
            for (var c = 0; c < k; c++)
                sums[c] = new double[dims];
            for (var i = 0; i < sample.Length; i++)
            {
                var c = labels[i];
                counts[c]++;
                var s = sums[c];
                var p = sample[i];
                for (var d = 0; d < dims; d++)
                    s[d] += p[d];
            }

            for (var c = 0; c < k; c++)
            {
                if (counts[c] == 0)
                {
                    centroids[c] = Normalized(sample[rng.Next(sample.Length)]); // re-seed empty cluster
                    continue;
                }

                var next = new float[dims];
                for (var d = 0; d < dims; d++)
                    next[d] = (float)sums[c][d];
                centroids[c] = TryNormalize(next) ? next : centroids[c];
            }
        }

        return centroids;
    }

    public static int[] Assign(IReadOnlyList<float[]> points, float[][] centroids)
    {
        var labels = new int[points.Count];
        AssignInto(points, centroids, labels);
        return labels;
    }

    /// <summary>Assigns <paramref name="count"/> points that <paramref name="load"/> writes into a per-thread buffer.</summary>
    public static int[] Assign(int count, int dims, Action<int, float[]> load, float[][] centroids)
    {
        var labels = new int[count];
        Parallel.For(0, count, () => new float[dims], (i, _, p) =>
        {
            load(i, p);
            labels[i] = Nearest(p, centroids);
            return p;
        }, _ => { });
        return labels;
    }

    private static int Nearest(float[] p, float[][] centroids)
    {
        var best = 0;
        var bestScore = float.NegativeInfinity;
        for (var c = 0; c < centroids.Length; c++)
        {
            var s = VectorMath.Dot(p, centroids[c]);
            if (s > bestScore)
            {
                bestScore = s;
                best = c;
            }
        }

        return best;
    }

    private static void AssignInto(IReadOnlyList<float[]> points, float[][] centroids, int[] labels)
    {
        Parallel.For(0, points.Count, i => labels[i] = Nearest(points[i], centroids));
    }

    private static float[] Normalized(float[] v)
    {
        var copy = (float[])v.Clone();
        return TryNormalize(copy) ? copy : copy;
    }

    private static bool TryNormalize(float[] v)
    {
        var n = VectorMath.Norm(v);
        if (n <= 0 || float.IsNaN(n))
            return false;
        for (var i = 0; i < v.Length; i++)
            v[i] /= n;
        return true;
    }
}
