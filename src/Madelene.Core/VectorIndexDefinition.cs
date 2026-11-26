using System.Text.Json;
using System.Text.Json.Nodes;
using Madelene.Filtering;

namespace Madelene;

public enum VectorSimilarity
{
    Cosine,
    DotProduct,
}

/// <summary>Engine tuning knobs. Defaults target 768-d CLIP-like embeddings on NVMe.</summary>
public sealed record MadeleneIndexOptions
{
    /// <summary>Width of a time bucket. Segments never span buckets, so time filters prune whole segments.</summary>
    public TimeSpan Bucket { get; init; } = TimeSpan.FromDays(1);

    /// <summary>Rows buffered in memory before they are sealed into an immutable segment.</summary>
    public int SealRows { get; init; } = 250_000;

    /// <summary>Segments at or above this size get an IVF layer; smaller ones are scanned flat.</summary>
    public int IvfMinRows { get; init; } = 1_000_000;

    /// <summary>IVF list count = factor × √rows.</summary>
    public double IvfListsPerSqrtRow { get; init; } = 2.0;

    /// <summary>
    /// Floor on the IVF lists probed per query, as a fraction of one segment's lists. The searched IVF segments
    /// share one budget: the floor is this × mean lists per segment × √(IVF segments searched).
    /// </summary>
    public double ProbeFraction { get; init; } = 0.03;

    /// <summary>Lists are probed (best-first across all searched segments) until they hold this × the rescore pool matching rows.</summary>
    public double ProbeCandidateFactor { get; init; } = 8.0;

    /// <summary>
    /// Floor on the 1-bit candidate pool that gets int8-rescored (numCandidates is raised to this, capped at 10,000).
    /// 1-bit estimates are noisy when top-k margins are thin (CLIP text→image), and rescoring is cheap.
    /// </summary>
    public int MinRescorePool { get; init; } = 1_000;

    /// <summary>The rescore pool is also raised to this fraction of the rows that match the filter.</summary>
    public double RescorePoolFraction { get; init; } = 0.001;

    /// <summary>
    /// The rescore pool is capped at 10,000 (MongoDB's numCandidates limit) or at this × limit, whichever is larger,
    /// so queries with a large limit still rescore a deep enough pool.
    /// </summary>
    public int RescorePoolPerLimit { get; init; } = 64;

    /// <summary>If a segment has this few rows matching the filter, scan them all instead of probing.</summary>
    public int ExactScanThreshold { get; init; } = 100_000;

    /// <summary>Compaction merges a bucket once it has this many segments.</summary>
    public int MergeFactor { get; init; } = 4;

    /// <summary>Compaction rewrites a segment once this fraction of its rows are deleted.</summary>
    public double TombstoneRewriteRatio { get; init; } = 0.2;

    /// <summary>
    /// After each merge, run a compacting garbage collection that returns the merge's memory to the OS.
    /// Turn off if the host process manages GC itself.
    /// </summary>
    public bool ReleaseMemoryAfterCompaction { get; init; } = true;

    /// <summary>Seed for the random rotation. Persisted, so it only matters at index creation.</summary>
    public int RotationSeed { get; init; } = 0x4D41444C;
}

/// <summary>
/// Definition of one vector index. Mirrors a MongoDB vector search index definition:
/// <code>
/// { "fields": [ { "type": "vector", "path": "...", "numDimensions": 768, "similarity": "cosine" },
///               { "type": "filter", "path": "tenantId" }, ... ],
///   "madelene": { "timeField": "timestamp", "bucket": "1.00:00:00", ... } }
/// </code>
/// </summary>
public sealed record VectorIndexDefinition
{
    private static readonly JsonSerializerOptions __json = new() { WriteIndented = true };

    public required string Name { get; init; }
    public required string VectorPath { get; init; }
    public required int Dimensions { get; init; }
    public VectorSimilarity Similarity { get; init; } = VectorSimilarity.Cosine;
    public IReadOnlyList<string> FilterPaths { get; init; } = [];

    /// <summary>Optional date field used for bucketing and range pruning. Stored as a column, not as terms.</summary>
    public string? TimePath { get; init; }

    public MadeleneIndexOptions Options { get; init; } = new();

    public bool IsFilterable(string path) => path == TimePath || FilterPaths.Contains(path, StringComparer.Ordinal);

    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(VectorPath);
        if (Dimensions is < 8 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(Dimensions), Dimensions, "numDimensions must be between 8 and 8192.");
        if (Options.SealRows < 1 || Options.IvfMinRows < 2 || Options.MergeFactor < 2)
            throw new ArgumentException("Invalid index options.");
    }

    /// <summary>Parses a MongoDB-style vector index definition (plus the optional "madelene" block).</summary>
    public static VectorIndexDefinition FromJson(string name, string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new VectorSearchRequestException("Index definition must be a JSON object.");
        var fields = root["fields"]?.AsArray() ?? throw new VectorSearchRequestException("Index definition needs 'fields'.");

        string? vectorPath = null;
        var dims = 0;
        var similarity = VectorSimilarity.Cosine;
        var filters = new List<string>();
        foreach (var field in fields)
        {
            var type = field?["type"]?.GetValue<string>();
            var path = field?["path"]?.GetValue<string>() ?? throw new VectorSearchRequestException("Every field needs a 'path'.");
            switch (type)
            {
                case "vector":
                    if (vectorPath != null)
                        throw new VectorSearchRequestException("Only one vector field per index is supported.");
                    vectorPath = path;
                    dims = field!["numDimensions"]?.GetValue<int>() ?? throw new VectorSearchRequestException("Vector field needs numDimensions.");
                    similarity = (field["similarity"]?.GetValue<string>() ?? "cosine") switch
                    {
                        "cosine" => VectorSimilarity.Cosine,
                        "dotProduct" => VectorSimilarity.DotProduct,
                        var other => throw new FilterNotSupportedException($"Similarity '{other}' is not supported (cosine, dotProduct)."),
                    };
                    break;
                case "filter":
                    filters.Add(path);
                    break;
                default:
                    throw new VectorSearchRequestException($"Unknown field type '{type}'.");
            }
        }

        var options = new MadeleneIndexOptions();
        string? timePath = null;
        if (root["madelene"] is JsonObject m)
        {
            timePath = m["timeField"]?.GetValue<string>();
            options = options with
            {
                Bucket = m["bucket"] is { } b ? TimeSpan.Parse(b.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : options.Bucket,
                SealRows = m["sealRows"]?.GetValue<int>() ?? options.SealRows,
                IvfMinRows = m["ivfMinRows"]?.GetValue<int>() ?? options.IvfMinRows,
                IvfListsPerSqrtRow = m["ivfListsPerSqrtRow"]?.GetValue<double>() ?? options.IvfListsPerSqrtRow,
                ProbeFraction = m["probeFraction"]?.GetValue<double>() ?? options.ProbeFraction,
                ProbeCandidateFactor = m["probeCandidateFactor"]?.GetValue<double>() ?? options.ProbeCandidateFactor,
                ExactScanThreshold = m["exactScanThreshold"]?.GetValue<int>() ?? options.ExactScanThreshold,
                MinRescorePool = m["minRescorePool"]?.GetValue<int>() ?? options.MinRescorePool,
                RescorePoolFraction = m["rescorePoolFraction"]?.GetValue<double>() ?? options.RescorePoolFraction,
                RescorePoolPerLimit = m["rescorePoolPerLimit"]?.GetValue<int>() ?? options.RescorePoolPerLimit,
                ReleaseMemoryAfterCompaction = m["releaseMemoryAfterCompaction"]?.GetValue<bool>() ?? options.ReleaseMemoryAfterCompaction,
                MergeFactor = m["mergeFactor"]?.GetValue<int>() ?? options.MergeFactor,
                TombstoneRewriteRatio = m["tombstoneRewriteRatio"]?.GetValue<double>() ?? options.TombstoneRewriteRatio,
                RotationSeed = m["rotationSeed"]?.GetValue<int>() ?? options.RotationSeed,
            };
        }

        var definition = new VectorIndexDefinition
        {
            Name = name,
            VectorPath = vectorPath ?? throw new VectorSearchRequestException("Index definition needs a vector field."),
            Dimensions = dims,
            Similarity = similarity,
            FilterPaths = filters.Where(f => f != timePath).Distinct(StringComparer.Ordinal).ToList(),
            TimePath = timePath,
            Options = options,
        };
        definition.Validate();
        return definition;
    }

    public string ToJson()
    {
        var fields = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "vector",
                ["path"] = VectorPath,
                ["numDimensions"] = Dimensions,
                ["similarity"] = Similarity == VectorSimilarity.Cosine ? "cosine" : "dotProduct",
            },
        };
        foreach (var path in FilterPaths)
            fields.Add(new JsonObject { ["type"] = "filter", ["path"] = path });
        if (TimePath != null)
            fields.Add(new JsonObject { ["type"] = "filter", ["path"] = TimePath });

        var root = new JsonObject
        {
            ["fields"] = fields,
            ["madelene"] = new JsonObject
            {
                ["timeField"] = TimePath,
                ["bucket"] = Options.Bucket.ToString("c"),
                ["sealRows"] = Options.SealRows,
                ["ivfMinRows"] = Options.IvfMinRows,
                ["ivfListsPerSqrtRow"] = Options.IvfListsPerSqrtRow,
                ["probeFraction"] = Options.ProbeFraction,
                ["probeCandidateFactor"] = Options.ProbeCandidateFactor,
                ["exactScanThreshold"] = Options.ExactScanThreshold,
                ["minRescorePool"] = Options.MinRescorePool,
                ["rescorePoolFraction"] = Options.RescorePoolFraction,
                ["rescorePoolPerLimit"] = Options.RescorePoolPerLimit,
                ["releaseMemoryAfterCompaction"] = Options.ReleaseMemoryAfterCompaction,
                ["mergeFactor"] = Options.MergeFactor,
                ["tombstoneRewriteRatio"] = Options.TombstoneRewriteRatio,
                ["rotationSeed"] = Options.RotationSeed,
            },
        };
        return root.ToJsonString(__json);
    }
}
