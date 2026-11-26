using System.Text.Json;
using Madelene.Filtering;

namespace Madelene.Search;

/// <summary>A parsed MongoDB <c>$vectorSearch</c> stage.</summary>
public sealed record VectorSearchRequest
{
    public const int MaxCandidates = 10_000;

    public required string Index { get; init; }
    public required string Path { get; init; }
    public required float[] QueryVector { get; init; }
    public required int Limit { get; init; }

    /// <summary>ANN candidate pool (rescored with int8 vectors). Required unless <see cref="Exact"/>.</summary>
    public int? NumCandidates { get; init; }

    public FilterNode Filter { get; init; } = FilterNode.MatchAll;

    /// <summary>Score every row that passes the filter instead of probing IVF lists.</summary>
    public bool Exact { get; init; }

    public void Validate()
    {
        if (Limit is < 1 or > MaxCandidates)
            throw new VectorSearchRequestException($"limit must be between 1 and {MaxCandidates}.");
        if (Exact)
        {
            if (NumCandidates != null)
                throw new VectorSearchRequestException("numCandidates is not allowed when exact is true.");
            return;
        }

        if (NumCandidates is not { } candidates)
            throw new VectorSearchRequestException("numCandidates is required for approximate search.");
        if (candidates < Limit)
            throw new VectorSearchRequestException("numCandidates must be greater than or equal to limit.");
        if (candidates > MaxCandidates)
            throw new VectorSearchRequestException($"numCandidates must be at most {MaxCandidates}.");
    }

    /// <summary>Parses either <c>{"$vectorSearch": {...}}</c> or the inner document.</summary>
    public static VectorSearchRequest FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$vectorSearch", out var inner))
            root = inner;
        if (root.ValueKind != JsonValueKind.Object)
            throw new VectorSearchRequestException("$vectorSearch must be a document.");

        string? index = null, path = null;
        float[]? vector = null;
        int? limit = null, candidates = null;
        var exact = false;
        var filter = FilterNode.MatchAll;
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name)
            {
                case "index": index = p.Value.GetString(); break;
                case "path": path = p.Value.GetString(); break;
                case "queryVector":
                    if (p.Value.ValueKind != JsonValueKind.Array)
                        throw new VectorSearchRequestException("queryVector must be an array of numbers.");
                    vector = p.Value.EnumerateArray().Select(e => e.GetSingle()).ToArray();
                    break;
                case "limit": limit = p.Value.GetInt32(); break;
                case "numCandidates": candidates = p.Value.GetInt32(); break;
                case "exact": exact = p.Value.GetBoolean(); break;
                case "filter": filter = JsonFilterParser.Parse(p.Value); break;
                default: throw new VectorSearchRequestException($"Unknown $vectorSearch field '{p.Name}'.");
            }
        }

        var request = new VectorSearchRequest
        {
            Index = index ?? throw new VectorSearchRequestException("index is required."),
            Path = path ?? throw new VectorSearchRequestException("path is required."),
            QueryVector = vector ?? throw new VectorSearchRequestException("queryVector is required."),
            Limit = limit ?? throw new VectorSearchRequestException("limit is required."),
            NumCandidates = candidates,
            Exact = exact,
            Filter = filter,
        };
        request.Validate();
        return request;
    }
}

/// <summary>One hit, with MongoDB's <c>vectorSearchScore</c> normalization: (1 + similarity) / 2.</summary>
public readonly record struct VectorSearchHit(string Id, double Score);

public sealed record SearchStats
{
    public int SegmentsTotal { get; init; }
    public int SegmentsPrunedByTime { get; init; }
    public int SegmentsFlat { get; init; }
    public int SegmentsIvf { get; init; }
    public long RowsMatchedByFilter { get; init; }
    public long RowsScored { get; init; }
    public int RowsRescored { get; init; }
    public int MemTableRowsMatched { get; init; }
    public double ElapsedMs { get; init; }

    /// <summary>Time pruning + filter evaluation into row bitsets.</summary>
    public double FilterMs { get; init; }

    /// <summary>Centroid scoring and IVF probe selection.</summary>
    public double PlanMs { get; init; }

    /// <summary>Scoring 1-bit codes (or int8 vectors for exact search).</summary>
    public double ScanMs { get; init; }

    /// <summary>Rescoring the candidate pool with int8 vectors (random reads when not cached).</summary>
    public double RescoreMs { get; init; }

    /// <summary>Exact scan of the in-memory write buffer.</summary>
    public double BufferMs { get; init; }
}

public sealed record VectorSearchResult(IReadOnlyList<VectorSearchHit> Hits, SearchStats Stats);
