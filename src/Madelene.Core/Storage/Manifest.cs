using System.Text.Json;

namespace Madelene.Storage;

/// <summary>
/// The durable state of an index: which segments are live, which tombstone generation each uses,
/// and the host's change-feed checkpoint (e.g. a MongoDB resume token) that the state corresponds to.
/// Replaced atomically (write temp file, then rename).
/// </summary>
internal sealed class Manifest
{
    private static readonly JsonSerializerOptions __json = new() { WriteIndented = true };

    public int Format { get; set; } = 1;
    public long NextSegmentId { get; set; } = 1;
    public long NextGeneration { get; set; } = 1;

    /// <summary>Opaque checkpoint supplied by the host. Every write applied before it is durable.</summary>
    public string? Checkpoint { get; set; }

    public DateTime? CheckpointUtc { get; set; }
    public List<ManifestSegment> Segments { get; set; } = [];

    public const string FileName = "manifest.json";

    public static Manifest LoadOrCreate(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
            return new Manifest();
        return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)) ?? throw new InvalidDataException($"Corrupt manifest '{path}'.");
    }

    public void Save(string directory)
    {
        var path = Path.Combine(directory, FileName);
        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(fs, this, __json);
            fs.Flush(flushToDisk: true);
        }

        FileRetry.Run(() => File.Move(tmp, path, overwrite: true));
    }
}

internal sealed record ManifestSegment(long Id, long TombstoneGeneration);
