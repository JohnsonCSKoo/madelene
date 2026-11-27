# Madelene

Embedded, disk-resident **filtered vector search for MongoDB data**, with a `$vectorSearch`-compatible
query surface. MongoDB stays the system of record; Madelene is a derived index you can drop and rebuild
at any time. It returns `_id`s and scores; MongoDB hydrates the documents.

Built for the case where MongoDB's own vector index no longer fits in RAM: billions of 768-d embeddings on
one machine with ~64–128 GB of RAM and NVMe storage.

Install: `dotnet add package Madelene.Core --prerelease`, plus `Madelene.Mongo` for the MongoDB integration.

> **Status: prototype (0.2).** Correctness is covered by tests (including a property test of the filter
> engine and an end-to-end MongoDB change-stream test). Benchmarks so far use synthetic data. Read
> [Known gaps](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/ARCHITECTURE.md#14-known-gaps) before using it for anything real.

| Document | What it covers |
|---|---|
| [docs/GUIDE.md](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/GUIDE.md) | Build, embed, configure, operate, tune and benchmark it. |
| [docs/ARCHITECTURE.md](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/ARCHITECTURE.md) | How it works: storage, encoding, query and write paths, durability, memory, performance. |
| [CHANGELOG.md](https://github.com/JohnsonCSKoo/madelene/blob/main/CHANGELOG.md) | Each iteration: what changed, why, and what was measured. |
| [docs/benchmarks/](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/benchmarks/README.md) | Comparisons with Milvus, Qdrant, SPTAG/SPFresh, and the full-text experiment. |

## How it works (one paragraph)

Writes land in an in-memory buffer that is searched exactly. The buffer is sealed into immutable,
time-bucketed **segments** on disk: 1-bit RaBitQ-style codes (96 B/vector at 768-d) for fast scoring,
int8 vectors (768 B) for rescoring, posting lists per filter value, a time column, and an IVF layer once a
segment is large. A query prunes segments by time, turns the filter into a row bitset, scores codes
(all matching rows when the filter is narrow, the nearest IVF lists otherwise), rescores the best
candidates with int8 vectors and returns MongoDB-normalized scores. Nothing per-vector has to live in
RAM. Details: [docs/ARCHITECTURE.md](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/ARCHITECTURE.md).

## Projects

| Path | What |
|---|---|
| `src/Madelene.Core` | The engine (no dependencies). |
| `src/Madelene.Mongo` | MongoDB integration: change-stream sync, BSON filters, `$vectorSearch` pipeline rewrite. |
| `tests/Madelene.Tests` | Unit, property and (opt-in) MongoDB integration tests. |
| `tools/Madelene.Bench` | Recall/latency benchmark on synthetic CLIP-like data or real `.npy` embeddings. |

## Quick start

### Embedded

```csharp
using var engine = new MadeleneEngine(@"D:\madelene");
var index = engine.CreateIndex(VectorIndexDefinition.FromJson("items", """
{
  "fields": [
    { "type": "vector", "path": "embedding", "numDimensions": 768, "similarity": "cosine" },
    { "type": "filter", "path": "sourceId" },
    { "type": "filter", "path": "tags" }
  ],
  "madelene": { "timeField": "timestamp", "bucket": "1.00:00:00" }
}
"""));

index.Upsert(new IndexDocument("item-1", vector,
    new Dictionary<string, IReadOnlyList<FieldValue>>
    {
        ["sourceId"] = [FieldValue.String("src7")],
        ["tags"] = [FieldValue.String("alpha"), FieldValue.String("beta")],
    },
    timeUnixMs: FieldValue.ToUnixMs(DateTime.UtcNow)));
index.Flush(checkpoint: "my-feed-position");   // durable from here

var result = engine.Search("""
{ "$vectorSearch": { "index": "items", "path": "embedding", "queryVector": [ ... ],
    "numCandidates": 400, "limit": 20,
    "filter": { "sourceId": "src7", "tags": { "$in": ["alpha"] },
                "timestamp": { "$gte": { "$date": "2026-11-01T00:00:00Z" } } } } }
""");
```

### Next to MongoDB

```csharp
var sync = new MongoIndexSync(engine, db.GetCollection<BsonDocument>("items"), definition);
_ = sync.RunAsync(stoppingToken);       // initial scan, then change stream with resume-token checkpoints

// Replace the first $vectorSearch stage of an existing pipeline; the rest runs unchanged on MongoDB.
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

## Supported `$vectorSearch` surface

* `index`, `path`, `queryVector` (array or BSON vector), `limit`, `numCandidates` (≤ 10,000), `exact`, `filter`.
* Filter operators: `$and`, `$or`, `$nor`, `$not`, `$eq`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`, `$lt`, `$lte`.
* Value types: string, number (any width, compared by value), boolean, ObjectId, date, UUID. Arrays use
  MongoDB multikey semantics.
* Similarity: `cosine`, `dotProduct`. Score is `(1 + similarity) / 2`, as in MongoDB.
* Anything else (`$exists`, null equality, regex, `euclidean`, …) throws `FilterNotSupportedException`,
  so callers can fall back to MongoDB.

## Benchmarks (synthetic)

Synthetic CLIP-like data: 1,000 clusters, a shared mean direction, and "text" queries shifted by a
modality-gap vector so the best matches have low cosine and thin margins. 768-d, k = 24,
`numCandidates` = 360 (raised to the 1,000 rescore floor). Machine: 24-core desktop, 64 GB RAM, local
NVMe. Recall is against exact float32 search over the rows matching the filter.

**100M vectors, 100 daily IVF segments of 1M rows, one tenant, 200 sources** (`--stream`)

The machine has 64 GB RAM, so the 95 GB index does not fit in the page cache; the rest is memory-mapped from
NVMe. Repeat pass (the second run of each query), 50 text-like queries per filter, version 0.2:

| Filter | Selectivity | Rows scored | Recall@24 | p50 | p99 | Recall@360 (5,400 cand) | p50 |
|---|---|---|---|---|---|---|---|
| tenant only (all 100 days) | 100% | 568k | 0.951 | 17.4 ms | 38.7 ms | 0.957 | 34.2 ms |
| + busiest source | 21.3% | 121k | 0.983 | 17.3 ms | 49.2 ms | 0.984 | 25.6 ms |
| + mid source | 0.28% | 281k | 0.984 | 21.3 ms | 45.6 ms | 0.994 | 26.5 ms |
| + 2 tokens (AND) | 28.4% | 161k | 0.970 | 17.8 ms | 66.1 ms | 0.984 | 25.0 ms |
| + token OR + NOT | 20.8% | 118k | 0.972 | 18.7 ms | 56.0 ms | 0.987 | 25.6 ms |
| + last 20% of time | 20.0% | 228k | 0.978 | 11.9 ms | 23.3 ms | 0.986 | 18.2 ms |
| + mid source + 5% of time | 0.014% | 14k | 0.993 | 4.7 ms | 15.5 ms | 0.992 | 6.5 ms |

First pass (colder pages): 5–25 ms p50. Throughput, mixing all seven filters at limit 24 / 360 candidates:

| Clients | QPS | p50 | p95 | p99 |
|---|---|---|---|---|
| 1 | 53.9 | 18 ms | 37 ms | 51 ms |
| 4 | 96.5 | 40 ms | 69 ms | 79 ms |
| 16 | 111.6 | 141 ms | 232 ms | 271 ms |

Version 0.1 on the same index took 14–120 ms p50 and peaked at 28 QPS: its planner gave every segment the whole
query's probe budget, so it scored 2–10M rows per broad query for the same recall. The comparison is in
[docs/ARCHITECTURE.md](https://github.com/JohnsonCSKoo/madelene/blob/main/docs/ARCHITECTURE.md#100m-vectors-01-against-02). Ingest 87–94k vectors/s, compaction of
400 sealed segments into 100 IVF segments ~64 min on an idle machine, disk 952 B/vector, managed heap 0.64 GB.

**1M vectors, one 1M-row IVF segment (2,000 lists), fully cached**

| Filter | Selectivity | Recall@24 | p50 | p99 |
|---|---|---|---|---|
| tenant only | 100% | 0.984 | 1.7 ms | 17.1 ms |
| + busiest source | 21.3% | 0.987 | 1.0 ms | 1.4 ms |
| + mid source | 0.27% | 0.988 | 0.9 ms | 6.7 ms |
| + 2 tokens (AND) | 28.2% | 0.990 | 1.0 ms | 1.4 ms |
| + token OR + NOT | 20.8% | 0.985 | 1.1 ms | 2.0 ms |
| + last 20% of time | 20.0% | 0.992 | 4.5 ms | 7.7 ms |
| + mid source + 5% of time | 0.016% | 0.995 | 11.9 ms | 15.3 ms |

Disk: 950 B/vector (≈ 2.9 TB for 3 B vectors). Ingest: ~91k vectors/s.
Compaction + IVF build of 1M rows: 36 s. Exact mode (`exact: true`) scores every matching row with int8
vectors: 6–14 ms at 1M.

Run it yourself:

```bash
dotnet run -c Release --project tools/Madelene.Bench -- --rows 1000000 --days 1
dotnet run -c Release --project tools/Madelene.Bench -- --stream --rows 100000000 --days 100 --ivf-min-rows 500000 --dir D:/bench/100m --keep --reuse
dotnet run -c Release --project tools/Madelene.Bench -- --vectors data.npy --query-vectors queries.npy --rows 5000000
```

These numbers say the engine works and where its costs are; they do not predict recall on real
embeddings. **Measure with your own vectors and queries** (`--vectors/--query-vectors`) before
choosing settings.

## Tests

```bash
dotnet test
```

The MongoDB end-to-end test (backfill, change-stream tail, updates that move a document out of the index,
deletes, pipeline rewrite + hydration, resume after restart) runs when `MADELENE_MONGO_URI` points at a
replica set; it uses and drops a throwaway database:

```bash
MADELENE_MONGO_URI="mongodb://localhost:27017/?directConnection=true" dotnet test
```

## License

Apache-2.0 (see [LICENSE](https://github.com/JohnsonCSKoo/madelene/blob/main/LICENSE)).
