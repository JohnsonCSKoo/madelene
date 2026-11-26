# Madelene guide

How to build, embed, configure, operate and tune Madelene. For how it works inside, read
[ARCHITECTURE.md](ARCHITECTURE.md). For what changed between versions, read [CHANGELOG.md](../CHANGELOG.md).

Madelene is a prototype. Measure it with your own vectors and queries before you rely on it.

## Contents

1. [When to use it](#1-when-to-use-it)
2. [Requirements and sizing](#2-requirements-and-sizing)
3. [Build and reference](#3-build-and-reference)
4. [Define an index](#4-define-an-index)
5. [Write data](#5-write-data)
6. [Query](#6-query)
7. [Run it next to MongoDB](#7-run-it-next-to-mongodb)
8. [Operate it](#8-operate-it)
9. [Tune it](#9-tune-it)
10. [Benchmark it](#10-benchmark-it)
11. [Limits](#11-limits)

## 1. When to use it

Use Madelene when:

* MongoDB (or another store) is the system of record, and you need `$vectorSearch`-style queries (a vector
  plus a filter) over more embeddings than an in-RAM graph index can hold on your hardware.
* Queries almost always carry a filter: a tenant, a source, a time range, a set of tags.
* You can run the index in-process, on one machine, with NVMe storage.

Don't use it when:

* The whole data set fits comfortably in RAM. An HNSW index (MongoDB Atlas Vector Search, Qdrant, Milvus)
  will be simpler and faster.
* You need replication, multiple writers across machines, or a network service out of the box. Madelene is a
  library. You host it.
* You need Euclidean similarity, full-text search, or filters outside MongoDB's `$vectorSearch` subset.

## 2. Requirements and sizing

* .NET 10 SDK (build) or runtime (run). Windows, Linux and macOS. The engine itself has no dependencies.
  `Madelene.Mongo` uses the official MongoDB driver.
* An SSD, ideally local NVMe. Segments are memory mapped, and anything not in the page cache is read from
  disk on demand.

Per vector at 768 dimensions with ~18 filter values per row:

| Resource | Per vector | 100M | 1B | 3B |
|---|---|---|---|---|
| Disk (whole index) | ~950 B | 95 GB | 0.95 TB | 2.9 TB |
| Hot data that should stay in the page cache (1-bit codes + factors) | 100 B | 10 GB | 100 GB | 300 GB |
| Managed heap of the engine (centroids, term dictionaries, tombstones) | ~7–10 B | 0.7–1 GB | 7–10 GB | 21–30 GB |

The hot data doesn't have to fit: queries with a time filter only touch the segments in their range. Queries over
all time read the probed lists of every segment, so the more of the codes the page cache holds, the faster they
are. The int8 vectors (768 B per vector, 81 % of the disk) are only read for the few thousand rows each query
rescores.

One index per tenant gives physical isolation and lets you size and drop each one separately.

Prefer fewer, larger segments: every query scores the centroids of every segment it searches (2·√rows lists per
segment, 3 KB each at 768-d). With 1B rows, 100 segments of 10M rows hold 0.63M centroids (1.9 GB, ~30 ms
of planning per all-time query); 1,000 segments of 1M rows hold 2M (6.1 GB, ~90 ms). Pick the `bucket` so a
bucket holds millions of rows, and filter by time where you can.

## 3. Build and reference

```bash
dotnet build -c Release
dotnet test
```

There is no NuGet package yet. Reference the projects directly:

```xml
<ProjectReference Include="path/to/src/Madelene.Core/Madelene.Core.csproj" />
<ProjectReference Include="path/to/src/Madelene.Mongo/Madelene.Mongo.csproj" />  <!-- if you sync from MongoDB -->
```

Use Server GC in the host process (`<ServerGarbageCollection>true</ServerGarbageCollection>`); compaction and
search are parallel. See [memory](#memory) for the GC settings that matter.

## 4. Define an index

An index definition is MongoDB's vector search index JSON plus an optional `madelene` block.

```json
{
  "fields": [
    { "type": "vector", "path": "embedding", "numDimensions": 768, "similarity": "cosine" },
    { "type": "filter", "path": "tenantId" },
    { "type": "filter", "path": "sourceId" },
    { "type": "filter", "path": "tags" },
    { "type": "filter", "path": "timestamp" }
  ],
  "madelene": { "timeField": "timestamp", "bucket": "1.00:00:00" }
}
```

```csharp
using var engine = new MadeleneEngine(@"D:\madelene");
var index = engine.CreateIndex(VectorIndexDefinition.FromJson("items.tenant1", json));
// on later starts: engine.TryGetIndex("items.tenant1", out var index)
```

* `similarity`: `cosine` or `dotProduct`. Cosine vectors are normalized on write and query.
* `filter` fields: any path you want to filter on. Arrays are multi-valued (MongoDB multikey semantics).
* `timeField`: a date field. It drives time buckets (segments never span a bucket, so a time filter skips
  whole segments) and retention. Strongly recommended for append-mostly data.

### `madelene` options

All are optional. The defaults target 768-d CLIP-like embeddings on NVMe.

| Key | Default | What it does |
|---|---|---|
| `timeField` | none | Time path for buckets and retention. |
| `bucket` | `1.00:00:00` | Width of a time bucket (`d.hh:mm:ss`). Aim for tens to hundreds of buckets across the data you keep. |
| `sealRows` | 250,000 | Rows held in the in-memory write buffer before they are sealed into a segment. Bounds the buffer's RAM (3 KB per row at 768-d). |
| `ivfMinRows` | 1,000,000 | Segments at least this large get an IVF layer; smaller ones are scanned flat. |
| `ivfListsPerSqrtRow` | 2 | IVF lists per segment = this × √rows (2,000 lists for 1M rows). |
| `probeFraction` | 0.03 | Floor on the IVF lists probed per query: this × mean lists per segment × √(IVF segments searched). |
| `probeCandidateFactor` | 8 | Lists are probed, best first across all searched segments, until they hold this × the rescore pool matching rows. |
| `exactScanThreshold` | 100,000 | A segment with at most this many rows matching the filter is scanned whole instead of probed. |
| `minRescorePool` | 1,000 | Floor on the 1-bit candidate pool that is rescored with int8 vectors. |
| `rescorePoolFraction` | 0.001 | The pool is also raised to this fraction of the matching rows. |
| `rescorePoolPerLimit` | 64 | The pool is capped at 10,000 or this × `limit`, whichever is larger. |
| `mergeFactor` | 4 | Compaction merges a bucket once it has this many segments. |
| `tombstoneRewriteRatio` | 0.2 | Compaction rewrites a segment once this fraction of its rows is deleted. |
| `releaseMemoryAfterCompaction` | true | After each merge, run a compacting GC that returns the merge's memory to the OS. |
| `rotationSeed` | fixed | Seed of the random rotation used by the 1-bit codes. Only read at creation. |

The definition (with all options) is stored in the index's `index.json`. Options are read when the index opens.

## 5. Write data

```csharp
index.Upsert(new IndexDocument(
    id: "65f1c2...",                                   // your _id as a string
    vector: embedding,                                 // float[numDimensions]
    fields: new Dictionary<string, IReadOnlyList<FieldValue>>
    {
        ["tenantId"] = [FieldValue.String("t1")],
        ["sourceId"] = [FieldValue.String("src7")],
        ["tags"] = [FieldValue.String("alpha"), FieldValue.String("beta")],
    },
    timeUnixMs: FieldValue.ToUnixMs(createdAtUtc)));

index.Flush(checkpoint: "my-feed-position");          // durable from here
```

| Call | Use |
|---|---|
| `Upsert(doc)` | Insert or replace by id. Tombstones any older copy in sealed segments. |
| `Insert(doc)` | For ids you know are new (a backfill into an empty index). Skips the per-segment id lookup that `Upsert` pays, which grows with the number of segments. |
| `UpsertMany(docs)` | `Upsert` in a loop. |
| `Delete(id)` | Delete by id. Returns whether a live copy existed. |
| `Flush(checkpoint)` | Seal the buffer, write tombstones and the manifest, store your checkpoint. Everything before the call is durable after it. |
| `DeleteOlderThan(cutoffUtc)` | Retention. Drops whole segments older than the cutoff without reading them; tombstones older rows in straddling segments. |
| `Compact(force: false)` | Merge small segments per bucket and rewrite segments with many deletes. |

Writes are visible to queries immediately: the write buffer is searched exactly.

**Durability.** There is no write-ahead log. Your change feed is the log. Store its position with `Flush`, and
after a restart replay from `index.Checkpoint`. Every operation is idempotent (upsert and delete by id), so
replaying more than needed is harmless. Writes after the last `Flush` are lost on a crash and come back with the
replay. If you have no change feed, call `Flush` after every batch you can't afford to lose.

**Threading.** Writes are serialized by a per-index lock: safe from several threads, but they don't run in
parallel, so one writer thread per index is enough. Queries run concurrently
with writes, seals and compactions: each query pins a consistent snapshot. Seals happen on the writer's thread
(seconds per 250k rows). Run `Compact` and `DeleteOlderThan` from a background job, not the writer.

## 6. Query

The query surface is MongoDB's `$vectorSearch` stage:

```csharp
var result = engine.Search("""
{ "$vectorSearch": {
    "index": "items.tenant1", "path": "embedding", "queryVector": [ ... ],
    "numCandidates": 360, "limit": 24,
    "filter": { "sourceId": { "$in": ["src7", "src9"] }, "tags": "alpha",
                "timestamp": { "$gte": { "$date": "2026-11-01T00:00:00Z" } } } } }
""");
foreach (var hit in result.Hits)
    Console.WriteLine($"{hit.Id} {hit.Score:F4}");
```

Or build a `VectorSearchRequest` in code and call `engine.Search(request)` or `index.Search(request)`.

* `limit` ≤ `numCandidates` ≤ 10,000, as in MongoDB. `exact: true` (without `numCandidates`) scores every
  matching row with its int8 vector: use it for narrow filters or to check recall.
* Filter operators: `$and`, `$or`, `$nor`, `$not`, `$eq`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`, `$lt`, `$lte`.
* Value types: string, number (any width, compared by value), boolean, ObjectId, date, UUID. Types never compare
  equal across brackets, as in MongoDB.
* Score: `(1 + similarity) / 2`, MongoDB's normalization.
* Anything else (`$exists`, null equality, `$regex`, whole-array equality, an unindexed path) throws
  `FilterNotSupportedException`. Catch it and run the query on MongoDB instead.

`result.Stats` tells you where the time went:

| Field | Meaning |
|---|---|
| `SegmentsTotal`, `SegmentsPrunedByTime` | Segments in the snapshot, and how many the time filter skipped. |
| `SegmentsFlat`, `SegmentsIvf` | Segments scanned whole vs probed through IVF lists. |
| `RowsMatchedByFilter`, `RowsScored`, `RowsRescored` | Rows the filter matched, 1-bit codes scored, int8 vectors rescored. |
| `FilterMs`, `PlanMs`, `ScanMs`, `RescoreMs`, `BufferMs` | Time per phase. |

## 7. Run it next to MongoDB

`Madelene.Mongo` keeps an index in sync with a collection and swaps the `$vectorSearch` stage of an existing
pipeline.

```csharp
var definition = VectorIndexDefinition.FromJson("items.tenant1", json);
var sync = new MongoIndexSync(engine, db.GetCollection<BsonDocument>("items"), definition, new MongoSyncOptions
{
    Match = new BsonDocument("tenantId", "t1"),   // which documents belong in this index
    Log = Console.WriteLine,
});
_ = sync.RunAsync(stoppingToken);   // initial scan, then a change stream with resume-token checkpoints

try
{
    var rewritten = MongoVectorSearch.RewritePipeline(engine, pipeline, out var stats);
    var docs = await collection.Aggregate<BsonDocument>(rewritten).ToListAsync();
}
catch (FilterNotSupportedException)
{
    // run the original pipeline on MongoDB's own $vectorSearch
}
```

* The first run records the cluster time, scans the collection (projected to the indexed paths) and flushes with
  that time as the checkpoint. After that it tails a change stream (`fullDocument: updateLookup`) from the
  checkpoint and flushes every `CheckpointEveryChanges` changes or `CheckpointInterval`.
* If the checkpoint has fallen off the oplog, or the collection is dropped or renamed, and `AutoResync` is on
  (default), the index is dropped and rebuilt, at most 3 times in a row.
* Documents whose vector is missing, the wrong size, NaN or all zeros are left out instead of stalling the stream.
* `RewritePipeline` turns `[ {$vectorSearch}, ...rest ]` into `[ {$match: {_id: {$in}}}, {$addFields: {score}},
  {$sort}, ...rest ]` and rewrites `{$meta: "vectorSearchScore"}`, so the rest of the pipeline runs unchanged.
  MongoDB re-checks everything after the first stage.

The change stream needs a replica set (a single-node replica set is enough).

## 8. Operate it

### Scheduled jobs

Run these from a background job per index, not from the writer:

* **Compaction**: `index.Compact()` every few minutes to hourly. It merges a bucket once it has `mergeFactor`
  segments and rewrites segments with ≥ 20 % deleted rows. A merge of 1M rows takes ~36 s on 24 cores and blocks
  nothing but other compactions.
* **Retention**: `index.DeleteOlderThan(DateTime.UtcNow - retention)` daily, then `Flush`.

### Memory

* The engine's managed heap is small (~7–10 B per vector). Everything else is memory-mapped files in the page
  cache, which the OS evicts as needed.
* The write buffer holds `sealRows` × 3 KB (768-d floats) at most: 0.75 GB at the default.
* A merge holds ~1 KB per row of the segments it merges (int8 vector, id, terms) plus the k-means sample:
  about 1–1.5 GB for a 1M-row bucket. Size buckets so a bucket's rows fit comfortably; at 10M rows per
  bucket, a merge needs ~10–15 GB while it runs.
* After each merge the engine runs a compacting GC that hands the merge's memory back to the OS
  (`releaseMemoryAfterCompaction`). Without it, .NET's Server GC keeps the memory committed for reuse: in a 10M
  benchmark a process held ~11 GB after daily compactions while its live heap was 0.14 GB.
* In a container, the GC limits itself to 75 % of the container's memory by default. To cap it explicitly, set
  `DOTNET_GCHeapHardLimit` (bytes, hex) or `System.GC.HeapHardLimit` in `runtimeconfig.json`. To trade some
  throughput for a smaller footprint, set `DOTNET_GCConserveMemory` (1–9).

### Monitoring

* `index.GetStatistics()`: segments, IVF segments, rows, live rows, buffered rows, disk bytes, checkpoint.
* `SearchStats` per query (see [Query](#6-query)).
* `engine.FailedIndexes`: indexes that failed to open (corrupt or truncated files, an old format).

### Recovery

The index is derived data. If an index fails to open, or you change its definition, drop it
(`engine.DropIndex(name)`) and rebuild it from MongoDB. A drop writes a durable marker first, so a crash during
the drop finishes it at the next start. Unreferenced segments and tombstone files are cleaned up at open.

Back up MongoDB, not Madelene.

## 9. Tune it

Start from the defaults and measure recall on your own vectors (see [Benchmark it](#10-benchmark-it)). Then:

| Symptom | Change |
|---|---|
| Recall too low on broad filters | Raise `probeCandidateFactor` (more rows scanned) or `minRescorePool` (more rows rescored). |
| Recall too low at large `limit` | Raise `rescorePoolPerLimit`. |
| Broad queries too slow | Lower `probeCandidateFactor` / `probeFraction`; check `RowsScored` in the stats. |
| Narrow filters too slow | Raise `exactScanThreshold` only if the matching rows are few; otherwise leave IVF probing on. |
| Too many small segments | Lower `sealRows` only if the write buffer's RAM matters; otherwise compact more often. |
| Merges use too much memory | Use smaller `bucket`s. |

`numCandidates` per query is the other lever: higher is more recall for more time, as in MongoDB.

Options are read from `index.json` when an index opens. To change them on an existing index, drop and rebuild
it, or (for the query-time options: `probe*`, `exactScanThreshold`, `*RescorePool*`) edit `index.json` while the
index is closed.

## 10. Benchmark it

`tools/Madelene.Bench` measures recall (against exact float32 search over the rows that match the filter),
latency and throughput on synthetic CLIP-like data, or on your own `.npy` embeddings.

```bash
# 1M rows in memory
dotnet run -c Release --project tools/Madelene.Bench -- --rows 1000000 --days 1

# 100M rows, streamed (rows are regenerated instead of held in RAM); keep and reuse the index across runs
dotnet run -c Release --project tools/Madelene.Bench -- --stream --rows 100000000 --days 100 \
  --ivf-min-rows 500000 --dir D:/bench/100m --keep --reuse

# your own vectors and queries
dotnet run -c Release --project tools/Madelene.Bench -- --vectors data.npy --query-vectors queries.npy --rows 5000000
```

Useful flags: `--queries`, `--k`, `--candidates`, `--days`, `--bucket-days`, `--sources`, `--vocab`, `--tokens`,
`--seal-rows`, `--ivf-min-rows`, `--probe-fraction`, `--exact-scan-threshold`, `--query-configs 24:360,360:5400`,
`--clients 1,4,16`, `--load-seconds`, `--seed`.

Results are written to `bench-results/*.json`, with the run's log (UTC times) and memory figures. The synthetic
data is built to be hard (thin margins between the best matches), but it is still synthetic: it says where the
costs are, not what recall you will get on real embeddings.

## 11. Limits

* One process, one machine, no replication. Recovery is a rebuild from MongoDB.
* Seals and compactions block the writer for seconds per 250k rows; a background builder is not built yet.
* Filter values are doubles: int64 values above 2^53 and Decimal128 lose exactness. Positional paths (`a.0.b`)
  are not supported. The time field must be a BSON date.
* `exact: true` is exact over int8 vectors, not float32.
* Merges re-encode from int8; the loss is small but compounds if a row is merged many times.
* No Euclidean similarity, `$exists`, null equality, whole-array equality or full-text search.
