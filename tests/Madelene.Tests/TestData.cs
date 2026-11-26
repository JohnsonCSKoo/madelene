using Madelene.Model;

namespace Madelene.Tests;

/// <summary>Deterministic synthetic data: clustered unit vectors plus a few filter fields.</summary>
internal static class TestData
{
    public static readonly DateTime Epoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static float[][] Centres(int count, int dims, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(_ => Normalize(Gaussian(rng, dims))).ToArray();
    }

    public static float[] Near(float[] centre, Random rng, float spread)
    {
        var v = Gaussian(rng, centre.Length);
        for (var i = 0; i < v.Length; i++)
            v[i] = centre[i] + spread * v[i] / MathF.Sqrt(centre.Length);
        return Normalize(v);
    }

    public static List<IndexDocument> Documents(int count, int dims, int seed, int days = 10)
    {
        var rng = new Random(seed);
        var centres = Centres(50, dims, seed + 1);
        var docs = new List<IndexDocument>(count);
        for (var i = 0; i < count; i++)
        {
            var tags = Enumerable.Range(0, rng.Next(0, 4)).Select(_ => FieldValue.String("tag" + rng.Next(30))).Distinct().ToList();
            var fields = new Dictionary<string, IReadOnlyList<FieldValue>>
            {
                ["src"] = [FieldValue.String("src" + rng.Next(20))],
                ["n"] = [FieldValue.Num(rng.Next(0, 100))],
                ["flag"] = [FieldValue.Bool(rng.Next(2) == 0)],
            };
            if (tags.Count > 0)
                fields["tags"] = tags;
            var time = FieldValue.ToUnixMs(Epoch.AddMinutes(rng.NextDouble() * days * 24 * 60));
            docs.Add(new IndexDocument($"doc{i}", Near(centres[rng.Next(centres.Length)], rng, 0.6f), fields, time));
        }

        return docs;
    }

    public static VectorIndexDefinition Definition(string name, int dims, MadeleneIndexOptions? options = null) => new()
    {
        Name = name,
        VectorPath = "embedding",
        Dimensions = dims,
        FilterPaths = ["src", "tags", "n", "flag"],
        TimePath = "ts",
        Options = options ?? new MadeleneIndexOptions { SealRows = 2_000, IvfMinRows = 6_000, ExactScanThreshold = 500 },
    };

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "madelene-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static float[] Gaussian(Random rng, int dims)
    {
        var v = new float[dims];
        for (var i = 0; i < dims; i++)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            v[i] = (float)(Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2));
        }

        return v;
    }

    public static float[] Normalize(float[] v)
    {
        var n = MathF.Sqrt(v.Sum(x => x * x));
        return v.Select(x => x / n).ToArray();
    }

    public static float Dot(float[] a, float[] b)
    {
        float s = 0;
        for (var i = 0; i < a.Length; i++)
            s += a[i] * b[i];
        return s;
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = TestData.TempDir();

    public string Path { get; }

    public void Dispose()
    {
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
