using Madelene.Numerics;
using Madelene.Quantization;
using Xunit;

namespace Madelene.Tests;

public sealed class QuantizationTests
{
    [Fact]
    public void RotationIsOrthogonal()
    {
        var rotation = RandomRotation.Create(64, seed: 1);
        var m = rotation.Matrix.ToArray();
        for (var a = 0; a < 64; a++)
        for (var b = 0; b < 64; b++)
        {
            var dot = VectorMath.Dot(m.AsSpan(a * 64, 64), m.AsSpan(b * 64, 64));
            Assert.InRange(dot, a == b ? 0.999f : -0.001f, a == b ? 1.001f : 0.001f);
        }
    }

    [Fact]
    public unsafe void OneBitEstimatorIsUnbiasedWithTheExpectedError()
    {
        const int dims = 768;
        var rng = new Random(3);
        var rotation = RandomRotation.Create(dims, seed: 2);
        var codeBytes = BinaryCodec.CodeBytes(dims);
        var lut = new float[codeBytes * 256];
        var errors = new List<double>();
        var rotated = new float[dims];
        var rotatedQuery = new float[dims];
        var code = new byte[codeBytes];
        for (var trial = 0; trial < 400; trial++)
        {
            var o = TestData.Normalize(TestData.Gaussian(rng, dims));
            var q = TestData.Normalize(TestData.Gaussian(rng, dims));
            rotation.Apply(o, rotated);
            var factor = BinaryCodec.Encode(rotated, code);
            rotation.Apply(q, rotatedQuery);
            var querySum = BinaryCodec.BuildLookup(rotatedQuery, lut);
            float setSum;
            fixed (byte* c = code)
            fixed (float* l = lut)
                setSum = BinaryCodec.SumSetBits(c, codeBytes, l);
            var estimate = factor * (2 * setSum - querySum);
            errors.Add(estimate - TestData.Dot(o, q));
        }

        var mean = errors.Average();
        var std = Math.Sqrt(errors.Average(e => (e - mean) * (e - mean)));
        // RaBitQ: error ≈ sqrt(1 - <x̄,o>²)/<x̄,o> / sqrt(D - 1) with <x̄,o> ≈ 0.8 for unit vectors → ≈ 0.027.
        Assert.InRange(mean, -0.006, 0.006);
        Assert.InRange(std, 0.015, 0.035);
    }

    [Fact]
    public void Int8RoundTripIsClose()
    {
        var rng = new Random(4);
        var v = TestData.Normalize(TestData.Gaussian(rng, 768));
        var codes = new sbyte[768];
        var scale = Int8Codec.Encode(v, codes);
        var back = new float[768];
        Int8Codec.Decode(codes, scale, back);
        Assert.True(TestData.Dot(v, back) > 0.9995f);
    }
}
