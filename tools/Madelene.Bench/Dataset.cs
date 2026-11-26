using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Madelene.Filtering;
using Madelene.Model;

namespace Madelene.Bench;

/// <summary>
/// Benchmark data. Synthetic vectors imitate CLIP image embeddings: clustered, anisotropic (a shared
/// mean direction) and queried by "text" vectors offset by a modality-gap direction, so the best matches
/// have low cosine similarity and thin margins — the hard case for compressed codes.
/// Real embeddings can be loaded from .npy files (float32, shape [rows, dims]).
/// </summary>
internal sealed partial class Dataset
{
    private Dataset(float[][] vectors, float[][] queries, BenchOptions options)
    {
        Vectors = vectors;
        Queries = queries;
        Dimensions = vectors[0].Length;
        Days = options.Days;
        Sources = options.Sources;
        var rng = new Random(options.Seed + 100);
        Source = new int[vectors.Length];
        Tokens = new int[vectors.Length][];
        Times = new long[vectors.Length];
        var sourceZipf = new Zipf(options.Sources, 1.1, rng);
        var tokenZipf = new Zipf(options.Vocabulary, 1.0, rng);
        var span = (long)TimeSpan.FromDays(options.Days).TotalMilliseconds;
        var startMs = FieldValue.ToUnixMs(Start);
        for (var i = 0; i < vectors.Length; i++)
        {
            Source[i] = sourceZipf.Next();
            var set = new HashSet<int>();
            while (set.Count < options.TokensPerRow)
                set.Add(tokenZipf.Next());
            Tokens[i] = set.ToArray();
            Times[i] = startMs + (long)(rng.NextDouble() * span);
        }
    }

    public DateTime Start { get; } = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    public int Days { get; }
    public int Sources { get; }
    public float[][] Vectors { get; }
    public float[][] Queries { get; }
    public int Dimensions { get; }
    public int[] Source { get; }
    public int[][] Tokens { get; }
    /// <summary>Unix ms.</summary>
    public long[] Times { get; }

    public static Dataset Create(BenchOptions options)
    {
        if (options.VectorsNpy != null)
        {
            var vectors = Npy.ReadFloat32Matrix(options.VectorsNpy, options.Rows);
            var queries = options.QueriesNpy != null
                ? Npy.ReadFloat32Matrix(options.QueriesNpy, options.Queries)
                : SampleQueries(vectors, options);
            return new Dataset(vectors.Select(Normalize).ToArray(), queries.Select(Normalize).ToArray(), options);
        }

        var rng = new Random(options.Seed);
        var dims = options.Dimensions;
        var mean = Normalize(Gaussian(rng, dims));
        var gap = Normalize(Gaussian(rng, dims));
        var centres = Enumerable.Range(0, options.Clusters).Select(_ => Normalize(Gaussian(rng, dims))).ToArray();
        var data = new float[options.Rows][];
        var seeds = Enumerable.Range(0, options.Rows).Select(_ => rng.Next()).ToArray();
        Parallel.For(0, options.Rows, i =>
        {
            var r = new Random(seeds[i]);
            data[i] = Mix(r, centres[r.Next(centres.Length)], mean, 0.8f, 0.9f, dims);
        });
        var q = new float[options.Queries][];
        for (var i = 0; i < q.Length; i++)
        {
            var v = Mix(rng, centres[rng.Next(centres.Length)], mean, 0.8f, 1.1f, dims);
            for (var d = 0; d < dims; d++)
                v[d] += (float)options.ModalityGap * gap[d];
            q[i] = Normalize(v);
        }

        return new Dataset(data, q, options);
    }

    public IndexDocument Document(int i) => new(
        i.ToString(CultureInfo.InvariantCulture),
        Vectors[i],
        new Dictionary<string, IReadOnlyList<FieldValue>>
        {
            ["tenantId"] = [FieldValue.String("t1")],
            ["sourceId"] = [FieldValue.String("src" + Source[i])],
            ["tags"] = Tokens[i].Select(t => FieldValue.String("tok" + t)).ToList(),
        },
        Times[i]);

    /// <summary>Row numbers matching the filter (reference evaluator).</summary>
    public int[] Matching(FilterNode filter)
    {
        var result = new List<int>();
        for (var i = 0; i < Vectors.Length; i++)
        {
            var fields = new Dictionary<string, IReadOnlyList<FieldValue>>
            {
                ["tenantId"] = [FieldValue.String("t1")],
                ["sourceId"] = [FieldValue.String("src" + Source[i])],
                ["tags"] = Tokens[i].Select(t => FieldValue.String("tok" + t)).ToList(),
            };
            if (DocumentFilterEvaluator.Matches(filter, fields, "timestamp", Times[i]))
                result.Add(i);
        }

        return result.ToArray();
    }

    /// <summary>Exact float32 top-k over the given rows.</summary>
    public HashSet<int> ExactTopK(float[] query, int[] rows, int k)
    {
        var scores = new float[rows.Length];
        Parallel.For(0, rows.Length, i => scores[i] = Numerics.VectorMath.Dot(query, Vectors[rows[i]]));
        var order = Enumerable.Range(0, rows.Length).OrderByDescending(i => scores[i]).Take(k);
        return order.Select(i => rows[i]).ToHashSet();
    }

    private static float[][] SampleQueries(float[][] vectors, BenchOptions options)
    {
        var rng = new Random(options.Seed);
        return Enumerable.Range(0, options.Queries).Select(_ =>
        {
            var v = (float[])vectors[rng.Next(vectors.Length)].Clone();
            for (var d = 0; d < v.Length; d++)
                v[d] += 0.05f * (float)(rng.NextDouble() - 0.5);
            return v;
        }).ToArray();
    }

    /// <summary>normalize(centre + meanWeight·mean + noise·g/√d): a unit-norm point whose noise term has norm ≈ noise.</summary>
    private static float[] Mix(Random rng, float[] centre, float[] mean, float meanWeight, float noise, int dims)
    {
        var v = Gaussian(rng, dims);
        var scale = noise / MathF.Sqrt(dims);
        for (var d = 0; d < dims; d++)
            v[d] = centre[d] + meanWeight * mean[d] + scale * v[d];
        return Normalize(v);
    }

    private static float[] Gaussian(Random rng, int dims)
    {
        var v = new float[dims];
        for (var i = 0; i < dims; i += 2)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            var r = System.Math.Sqrt(-2 * System.Math.Log(u1));
            v[i] = (float)(r * System.Math.Cos(2 * System.Math.PI * u2));
            if (i + 1 < dims)
                v[i + 1] = (float)(r * System.Math.Sin(2 * System.Math.PI * u2));
        }

        return v;
    }

    private static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        for (var i = 0; i < v.Length; i++)
            v[i] /= n;
        return v;
    }

    private sealed class Zipf
    {
        private readonly double[] _cdf;
        private readonly Random _rng;

        public Zipf(int n, double s, Random rng)
        {
            _rng = rng;
            _cdf = new double[n];
            double sum = 0;
            for (var i = 0; i < n; i++)
                _cdf[i] = sum += 1.0 / System.Math.Pow(i + 1, s);
            for (var i = 0; i < n; i++)
                _cdf[i] /= sum;
        }

        public int Next()
        {
            var idx = Array.BinarySearch(_cdf, _rng.NextDouble());
            return idx >= 0 ? idx : System.Math.Min(~idx, _cdf.Length - 1);
        }
    }

    /// <summary>Minimal .npy reader for little-endian float32 C-order 2-D arrays.</summary>
    private static partial class Npy
    {
        public static float[][] ReadFloat32Matrix(string path, int maxRows)
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadByte() != 0x93 || Encoding.ASCII.GetString(br.ReadBytes(5)) != "NUMPY")
                throw new InvalidDataException($"{path} is not a .npy file.");
            var major = br.ReadByte();
            br.ReadByte();
            var headerLength = major == 1 ? br.ReadUInt16() : (int)br.ReadUInt32();
            var header = Encoding.ASCII.GetString(br.ReadBytes(headerLength));
            if (!header.Contains("'<f4'") || header.Contains("'fortran_order': True"))
                throw new InvalidDataException("Only little-endian float32 C-order arrays are supported.");
            var shape = ShapeRegex().Match(header);
            var rows = int.Parse(shape.Groups[1].Value, CultureInfo.InvariantCulture);
            var dims = int.Parse(shape.Groups[2].Value, CultureInfo.InvariantCulture);
            rows = System.Math.Min(rows, maxRows);
            var result = new float[rows][];
            var buffer = new byte[dims * 4];
            for (var r = 0; r < rows; r++)
            {
                br.BaseStream.ReadExactly(buffer);
                result[r] = new float[dims];
                Buffer.BlockCopy(buffer, 0, result[r], 0, buffer.Length);
            }

            return result;
        }

        [GeneratedRegex(@"'shape':\s*\((\d+),\s*(\d+)\)")]
        private static partial Regex ShapeRegex();
    }
}
