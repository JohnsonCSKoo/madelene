using Madelene.Filtering;
using Madelene.Model;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Madelene.Mongo;

public sealed record MongoSyncOptions
{
    /// <summary>
    /// Optional MongoDB-style filter selecting which documents belong to the index
    /// (e.g. <c>{ tenantId: "..." }</c>). Same operator subset as vector search filters.
    /// </summary>
    public BsonDocument? Match { get; init; }

    public int BatchSize { get; init; } = 1_000;

    /// <summary>Checkpoint (make durable + store the resume token) after this many applied changes...</summary>
    public int CheckpointEveryChanges { get; init; } = 50_000;

    /// <summary>...or after this much time, whichever comes first. Idle streams still advance the token.</summary>
    public TimeSpan CheckpointInterval { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan MaxAwaitTime { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>When the oplog no longer holds the checkpoint, drop the index and rebuild it from a fresh scan.</summary>
    public bool AutoResync { get; init; } = true;

    public Action<string>? Log { get; init; }
}

/// <summary>
/// Keeps one Madelene index in sync with a MongoDB collection.
/// <list type="number">
/// <item>No checkpoint yet: record the cluster time, scan the collection, flush with that time as the checkpoint.</item>
/// <item>Then tail a change stream from the checkpoint. The stream acts as the write-ahead log: the index
/// only persists sealed segments plus the resume token, and replays from it after a restart.</item>
/// </list>
/// MongoDB stays the source of truth; the index can always be dropped and rebuilt.
/// </summary>
public sealed class MongoIndexSync
{
    private const string TimestampPrefix = "ts:";
    private const string ResumeTokenPrefix = "rt:";

    private readonly MadeleneEngine _engine;
    private readonly IMongoCollection<BsonDocument> _collection;
    private readonly VectorIndexDefinition _definition;
    private readonly MongoSyncOptions _options;
    private readonly BsonDocumentMapper _mapper;
    private readonly FilterNode _match;
    private readonly IReadOnlyList<string> _matchPaths;

    public MongoIndexSync(MadeleneEngine engine, IMongoCollection<BsonDocument> collection, VectorIndexDefinition definition, MongoSyncOptions? options = null)
    {
        _engine = engine;
        _collection = collection;
        _definition = definition;
        _options = options ?? new MongoSyncOptions();
        _mapper = new BsonDocumentMapper(definition);
        _match = _options.Match is { } m ? BsonFilterParser.Parse(m) : FilterNode.MatchAll;
        _matchPaths = _options.Match is { } mm ? CollectPaths(BsonFilterParser.Parse(mm)).Distinct().ToList() : [];
        Index = engine.CreateIndex(definition);
    }

    public VectorIndex Index { get; private set; }

    /// <summary>Changes applied by the tailing loop since start (for monitoring).</summary>
    public long ChangesApplied { get; private set; }

    /// <summary>Wall-clock time of the last change applied from the stream.</summary>
    public DateTime? LastChangeUtc { get; private set; }

    /// <summary>Scans the collection if the index has never been synced, then tails changes until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var rebuilds = 0;
        DateTime? syncedAt = null; // when the last full scan finished
        while (true)
        {
            try
            {
                if (Index.Checkpoint == null)
                {
                    // An interrupted initial sync leaves auto-sealed segments without a checkpoint. Rescanning on
                    // top of them would keep documents deleted in the meantime forever, so start clean.
                    var stats = Index.GetStatistics();
                    if (stats.SegmentRows > 0 || stats.BufferedRows > 0)
                        Rebuild("an earlier initial sync did not finish");
                    await InitialSyncAsync(cancellationToken);
                    syncedAt = DateTime.UtcNow;
                }

                await TailAsync(cancellationToken);
                return;
            }
            catch (Exception ex) when (_options.AutoResync && IsResyncRequired(ex) && !cancellationToken.IsCancellationRequested)
            {
                // e.g. ChangeStreamHistoryLost: the oplog no longer covers our checkpoint (or the collection was
                // dropped/renamed). If even a fresh scan can't keep up with the oplog, stop instead of looping.
                // Measured from the end of the last scan: if a scan takes longer than the oplog window, the
                // failure comes right after it and the counter keeps growing.
                if (syncedAt is { } t && DateTime.UtcNow - t > TimeSpan.FromHours(1))
                    rebuilds = 0; // the last rebuild held up for a while: not a loop
                if (++rebuilds > MaxConsecutiveRebuilds)
                    throw new InvalidOperationException($"Index '{_definition.Name}' was rebuilt {MaxConsecutiveRebuilds} times in a row; the oplog window is probably shorter than a full scan.", ex);
                Rebuild(ex.Message);
            }
        }
    }

    private const int MaxConsecutiveRebuilds = 3;

    private static bool IsResyncRequired(Exception ex) =>
        ex is MongoSyncInvalidatedException || ex is MongoCommandException { Code: 286 or 280 };

    private void Rebuild(string reason)
    {
        _options.Log?.Invoke($"Rebuilding index '{_definition.Name}': {reason}");
        _engine.DropIndex(_definition.Name);
        Index = _engine.CreateIndex(_definition);
    }

    /// <summary>Full scan; returns the number of documents indexed. Safe to call on an empty index only.</summary>
    public async Task<long> InitialSyncAsync(CancellationToken cancellationToken)
    {
        var existing = Index.GetStatistics();
        if (existing.SegmentRows > 0 || existing.BufferedRows > 0 || Index.Checkpoint != null)
            throw new InvalidOperationException($"Index '{_definition.Name}' is not empty; drop it before a full scan (the scan inserts without de-duplicating).");
        var startTime = await CurrentOperationTimeAsync(cancellationToken);
        _options.Log?.Invoke($"Initial sync of '{_definition.Name}' from cluster time {startTime}.");

        var filter = new BsonDocument(_definition.VectorPath, new BsonDocument("$exists", true));
        if (_options.Match != null)
            filter = new BsonDocument("$and", new BsonArray { _options.Match, filter });
        var projection = new BsonDocument(ProjectionPaths().Select(p => new BsonElement(p, 1)));

        long indexed = 0;
        using var cursor = await _collection.FindAsync(filter, new FindOptions<BsonDocument> { Projection = projection, BatchSize = _options.BatchSize }, cancellationToken);
        while (await cursor.MoveNextAsync(cancellationToken))
        {
            foreach (var doc in cursor.Current)
            {
                var mapped = TryMap(doc);
                if (mapped == null)
                    continue;
                Index.Insert(mapped); // the index starts empty and a collection scan yields each _id once
                indexed++;
            }
        }

        // Changes made during the scan are replayed from startTime; upserts and deletes are idempotent.
        Index.Flush(TimestampPrefix + startTime.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        _options.Log?.Invoke($"Initial sync of '{_definition.Name}' indexed {indexed} documents.");
        return indexed;
    }

    private async Task TailAsync(CancellationToken cancellationToken)
    {
        var options = new ChangeStreamOptions
        {
            FullDocument = ChangeStreamFullDocumentOption.UpdateLookup,
            BatchSize = _options.BatchSize,
            MaxAwaitTime = _options.MaxAwaitTime,
        };
        var checkpoint = Index.Checkpoint!;
        if (checkpoint.StartsWith(TimestampPrefix, StringComparison.Ordinal))
            options.StartAtOperationTime = new BsonTimestamp(long.Parse(checkpoint[TimestampPrefix.Length..], System.Globalization.CultureInfo.InvariantCulture));
        else if (checkpoint.StartsWith(ResumeTokenPrefix, StringComparison.Ordinal))
            options.ResumeAfter = BsonDocument.Parse(checkpoint[ResumeTokenPrefix.Length..]);
        else
            throw new InvalidOperationException($"Unrecognized checkpoint '{checkpoint}'.");

        // Sub-field projections drop fullDocument._id (only the top-level _id, the resume token, is implicit).
        var projected = new BsonDocument { { "operationType", 1 }, { "documentKey", 1 }, { "fullDocument._id", 1 } };
        foreach (var path in ProjectionPaths())
            projected["fullDocument." + path] = 1;
        var pipeline = PipelineDefinition<ChangeStreamDocument<BsonDocument>, ChangeStreamDocument<BsonDocument>>.Create(
        [
            new BsonDocument("$match", new BsonDocument("operationType", new BsonDocument("$in", new BsonArray { "insert", "update", "replace", "delete", "drop", "rename", "dropDatabase", "invalidate" }))),
            new BsonDocument("$project", projected),
        ]);

        using var cursor = await _collection.WatchAsync(pipeline, options, cancellationToken);
        var sinceCheckpoint = 0;
        var lastCheckpoint = DateTime.UtcNow;
        BsonDocument? token = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasBatch;
            try
            {
                hasBatch = await cursor.MoveNextAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!hasBatch)
                break;
            foreach (var change in cursor.Current)
            {
                Apply(change);
                token = change.ResumeToken;
                sinceCheckpoint++;
                ChangesApplied++;
                LastChangeUtc = DateTime.UtcNow;
            }

            // Even an empty batch carries a post-batch resume token, so idle streams keep a fresh checkpoint.
            token = cursor.GetResumeToken() ?? token;
            if (token != null && (sinceCheckpoint >= _options.CheckpointEveryChanges || DateTime.UtcNow - lastCheckpoint >= _options.CheckpointInterval))
            {
                Index.Flush(ResumeTokenPrefix + token.ToJson());
                sinceCheckpoint = 0;
                lastCheckpoint = DateTime.UtcNow;
            }
        }

        if (token != null)
            Index.Flush(ResumeTokenPrefix + token.ToJson());
    }

    private void Apply(ChangeStreamDocument<BsonDocument> change)
    {
        switch (change.OperationType)
        {
            case ChangeStreamOperationType.Insert:
            case ChangeStreamOperationType.Update:
            case ChangeStreamOperationType.Replace:
            {
                var id = IdCodec.Encode(change.DocumentKey["_id"]);
                var doc = change.FullDocument;
                var mapped = doc != null && MatchesSyncFilter(doc) ? TryMap(doc) : null;
                if (mapped == null)
                    Index.Delete(id); // deleted since, filtered out, or vector removed
                else
                    Index.Upsert(mapped);
                break;
            }

            case ChangeStreamOperationType.Delete:
                Index.Delete(IdCodec.Encode(change.DocumentKey["_id"]));
                break;
            default:
                throw new MongoSyncInvalidatedException($"Collection event '{change.OperationType}' invalidates index '{_definition.Name}'; it must be rebuilt.");
        }
    }

    /// <summary>Maps a document, treating values the index can't represent as "not in the index" rather than stalling the stream.</summary>
    private IndexDocument? TryMap(BsonDocument doc)
    {
        try
        {
            return _mapper.Map(doc);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException or OverflowException or InvalidCastException)
        {
            _options.Log?.Invoke($"Skipping document {doc.GetValue("_id", BsonNull.Value)} in '{_definition.Name}': {ex.Message}");
            return null;
        }
    }

    private bool MatchesSyncFilter(BsonDocument doc)
    {
        if (_match is AllNode)
            return true;
        var fields = new Dictionary<string, IReadOnlyList<FieldValue>>(StringComparer.Ordinal);
        foreach (var path in _matchPaths)
        {
            fields[path] = BsonFields.Values(doc, path)
                .Select(BsonFields.ToFieldValue)
                .Where(v => v != null)
                .Select(v => v!.Value)
                .ToList();
        }

        return DocumentFilterEvaluator.Matches(_match, fields, timePath: null, timeUnixMs: null);
    }

    private async Task<BsonTimestamp> CurrentOperationTimeAsync(CancellationToken cancellationToken)
    {
        var db = _collection.Database;
        using var session = await db.Client.StartSessionAsync(cancellationToken: cancellationToken);
        var reply = await db.RunCommandAsync<BsonDocument>(session, new BsonDocument("ping", 1), cancellationToken: cancellationToken);
        if (reply.TryGetValue("operationTime", out var ts) && ts is BsonTimestamp timestamp)
            return timestamp;
        return session.OperationTime ?? throw new NotSupportedException("Change streams need a replica set or sharded cluster (no operationTime available).");
    }

    /// <summary>Indexed + match paths without overlaps ("a" covers "a.b"), which MongoDB rejects as a path collision.</summary>
    private List<string> ProjectionPaths()
    {
        var paths = _mapper.ProjectedPaths.Concat(_matchPaths)
            .Where(p => p != "_id" && !p.StartsWith("_id.", StringComparison.Ordinal)) // _id is always projected
            .Distinct().OrderBy(p => p.Length).ToList();
        var kept = new List<string>();
        foreach (var path in paths)
            if (!kept.Any(k => path.StartsWith(k + ".", StringComparison.Ordinal)))
                kept.Add(path);
        return kept;
    }

    private static IEnumerable<string> CollectPaths(FilterNode node) => node switch
    {
        AndNode a => a.Children.SelectMany(CollectPaths),
        OrNode o => o.Children.SelectMany(CollectPaths),
        NorNode n => n.Children.SelectMany(CollectPaths),
        NotNode n => CollectPaths(n.Child),
        InNode i => [i.Path],
        RangeNode r => [r.Path],
        _ => [],
    };
}

/// <summary>The watched collection was dropped, renamed or the stream invalidated; the index must be rebuilt.</summary>
public sealed class MongoSyncInvalidatedException(string message) : Exception(message);
