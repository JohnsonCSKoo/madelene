using Madelene.Numerics;

namespace Madelene.Quantization;

/// <summary>
/// A random orthogonal matrix (row-major). Rotating before taking sign bits spreads energy evenly
/// across dimensions, which is what makes 1-bit codes unbiased estimators (RaBitQ).
/// </summary>
internal sealed class RandomRotation
{
    private readonly float[] _matrix;

    private RandomRotation(int dimensions, float[] matrix)
    {
        Dimensions = dimensions;
        _matrix = matrix;
    }

    public int Dimensions { get; }

    public ReadOnlySpan<float> Matrix => _matrix;

    public static RandomRotation Create(int dimensions, int seed)
    {
        var rng = new Random(seed);
        var m = new float[dimensions * dimensions];
        var row = new double[dimensions];
        var basis = new double[dimensions][];
        for (var r = 0; r < dimensions; r++)
        {
            // Modified Gram-Schmidt against previous rows; retry on (astronomically unlikely) degeneracy.
            while (true)
            {
                for (var i = 0; i < dimensions; i++)
                    row[i] = Gaussian(rng);
                for (var p = 0; p < r; p++)
                {
                    var prev = basis[p];
                    double dot = 0;
                    for (var i = 0; i < dimensions; i++)
                        dot += row[i] * prev[i];
                    for (var i = 0; i < dimensions; i++)
                        row[i] -= dot * prev[i];
                }

                double norm = 0;
                for (var i = 0; i < dimensions; i++)
                    norm += row[i] * row[i];
                norm = System.Math.Sqrt(norm);
                if (norm < 1e-6)
                    continue;
                var unit = new double[dimensions];
                for (var i = 0; i < dimensions; i++)
                {
                    unit[i] = row[i] / norm;
                    m[r * dimensions + i] = (float)unit[i];
                }

                basis[r] = unit;
                break;
            }
        }

        return new RandomRotation(dimensions, m);
    }

    public static RandomRotation Load(string path, int dimensions)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != dimensions * dimensions * sizeof(float))
            throw new InvalidDataException($"Rotation file '{path}' has the wrong size for {dimensions} dimensions.");
        var m = new float[dimensions * dimensions];
        Buffer.BlockCopy(bytes, 0, m, 0, bytes.Length);
        return new RandomRotation(dimensions, m);
    }

    public void Save(string path)
    {
        Storage.BinaryFiles.Write<float>(path, _matrix);
    }

    public void Apply(ReadOnlySpan<float> x, Span<float> y) => VectorMath.MatVec(_matrix, x, y);

    private static double Gaussian(Random rng)
    {
        // Box-Muller
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        return System.Math.Sqrt(-2.0 * System.Math.Log(u1)) * System.Math.Cos(2 * System.Math.PI * u2);
    }
}
