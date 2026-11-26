using System.Runtime.InteropServices;
using System.Text.Json;

namespace Madelene.Storage;

/// <summary>
/// A row handed to the segment builder. The vector is either full precision (<see cref="Vector"/>, rows from the
/// write buffer, already normalized for cosine indexes) or int8 with its scale (<see cref="Int8"/>, rows carried
/// over by compaction, 4× smaller while a merge holds them in memory; decoded and normalized on demand).
/// </summary>
internal sealed class BuildRow
{
    public required string Id { get; init; }
    public float[]? Vector { get; init; }
    public sbyte[]? Int8 { get; init; }
    public float Int8Scale { get; init; }

    /// <summary>Normalize the decoded int8 vector (cosine indexes).</summary>
    public bool NormalizeDecoded { get; init; }

    /// <summary>Term keys as produced by <see cref="TermKeys.Of"/>.</summary>
    public required IReadOnlyList<string> Terms { get; init; }

    /// <summary>Time field as Unix ms, or <see cref="long.MinValue"/> if missing.</summary>
    public long Time { get; init; } = long.MinValue;

    /// <summary>Writes the row's vector (decoded from int8 if needed) into <paramref name="destination"/>.</summary>
    public void CopyVectorTo(Span<float> destination)
    {
        if (Vector != null)
        {
            Vector.CopyTo(destination);
            return;
        }

        Madelene.Quantization.Int8Codec.Decode(Int8!, Int8Scale, destination);
        if (NormalizeDecoded)
            Madelene.Numerics.VectorMath.NormalizeInPlace(destination);
    }
}

internal static class TermKeys
{
    public const char Separator = '\u001f';

    public static string Of(string path, Model.FieldValue value) => path + Separator + value.ToTerm();

    public static string Prefix(string path, Model.FieldKind kind) => path + Separator + (char)kind + ":";
}

internal sealed record SegmentMeta
{
    /// <summary>2 = time column in Unix ms. Older formats are rejected (rebuild the index).</summary>
    public const int CurrentFormat = 2;

    public int Format { get; init; } = CurrentFormat;
    public long Id { get; init; }
    public int Rows { get; init; }
    public int Dimensions { get; init; }
    public int CodeBytes { get; init; }
    public int Clusters { get; init; }
    public long MinTime { get; init; } = long.MinValue;
    public long MaxTime { get; init; } = long.MinValue;
    public bool HasTime { get; init; }
    public long Bucket { get; init; }
    public DateTime CreatedUtc { get; init; }

    private static readonly JsonSerializerOptions __json = new() { WriteIndented = true };

    public void Save(string path) => BinaryFiles.WriteDurable(path, System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this, __json)));

    public static SegmentMeta Load(string path) =>
        JsonSerializer.Deserialize<SegmentMeta>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Bad segment meta '{path}'.");
}

/// <summary>File names inside a segment directory.</summary>
internal static class SegmentFileNames
{
    public const string Meta = "meta.json";
    public const string Centroids = "centroids.f32";
    public const string ClusterOffsets = "clusters.i32";
    public const string Codes = "codes.u8";
    public const string Factors = "factors.f32";
    public const string Vectors = "vectors.i8";
    public const string Scales = "scales.f32";
    public const string Time = "time.i64";
    public const string IdData = "ids.dat";
    public const string IdOffsets = "ids.off";
    public const string IdHashes = "idhash.u64";
    public const string IdHashRows = "idhash.i32";
    public const string Bloom = "bloom.u64";
    public const string TermData = "terms.dat";
    public const string TermOffsets = "terms.off";
    public const string PostingOffsets = "post.off";
    public const string PostingCounts = "post.cnt";
    public const string PostingKinds = "post.kind";
    public const string Postings = "postings.bin";

    public static string Tombstones(long generation) => $"tomb-{generation:D6}.u64";
}

internal static class BinaryFiles
{
    /// <summary>Creates a file whose contents are flushed to the device (fsync) when the stream is disposed.</summary>
    public static FileStream CreateDurable(string path) => new DurableFileStream(path);

    public static void Write<T>(string path, ReadOnlySpan<T> data) where T : unmanaged
    {
        using var fs = CreateDurable(path);
        fs.Write(MemoryMarshal.AsBytes(data));
    }

    public static void WriteDurable(string path, ReadOnlySpan<byte> data) => Write(path, data);

    private sealed class DurableFileStream(string path) : FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20)
    {
        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                    Flush(flushToDisk: true); // may throw (disk full): still release the handle below
            }
            finally
            {
                base.Dispose(disposing);
            }
        }
    }

    public static T[] Read<T>(string path) where T : unmanaged
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length % Marshal.SizeOf<T>() != 0)
            throw new InvalidDataException($"'{path}' has a size that is not a multiple of {typeof(T).Name}.");
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    public static void Append<T>(Stream stream, ReadOnlySpan<T> data) where T : unmanaged => stream.Write(MemoryMarshal.AsBytes(data));
}
