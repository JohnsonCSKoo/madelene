# Madelene architecture

Status: prototype (0.2). This document describes what is implemented, why, and what isn't done yet. For how to
use it, read [GUIDE.md](GUIDE.md). For what changed between versions, read [CHANGELOG.md](../CHANGELOG.md).

## Contents

1. [Goal and budget](#1-goal-and-budget)
2. [How it fits next to MongoDB](#2-how-it-fits-next-to-mongodb)
3. [Projects](#3-projects)
4. [Data model](#4-data-model)
5. [Storage](#5-storage)
6. [Vector encoding](#6-vector-encoding)
7. [Query path](#7-query-path)
8. [Write path](#8-write-path)
9. [Durability and recovery](#9-durability-and-recovery)
10. [MongoDB integration](#10-mongodb-integration)
11. [Memory](#11-memory)
12. [Performance](#12-performance)
13. [Alternatives that were measured](#13-alternatives-that-were-measured)
14. [Known gaps](#14-known-gaps)

## 1. Goal and budget

Serve MongoDB-style `$vectorSearch` queries (a vector plus a pre-filter) over billions of embeddings on one
machine with modest RAM, while MongoDB stays the system of record. Madelene is a *derived* index: it can be
dropped and rebuilt from MongoDB at any time, and it only returns ids and scores. The host hydrates documents
from MongoDB and keeps its own exact rechecks.

The design goal that drives every decision: **128 GB of RAM for 3 billion 768-d vectors**, about 43 bytes of RAM per
vector for everything, and p50 latency under 1 s. So nothing per-vector has to live in RAM.
Per-vector data lives on SSD and is memory mapped; RAM holds routing structures (IVF centroids, term
dictionaries, tombstones) and whatever the page cache keeps.

The defaults and benchmarks use a generic multi-tenant shape: each row has a tenant, a source, a timestamp and a
set of string tags; data arrives continuously and old data is deleted by time; queries filter by tenant and often
by source, time range and tag combinations. The benchmark's parameters (200 sources, ~18 tags per row from a
500-word vocabulary) are arbitrary choices for testing.

## 2. How it fits next to MongoDB

The application keeps its aggregation pipeline. The seam swaps only the first stage, so everything after
`$vectorSearch` runs unchanged in MongoDB. Turning Madelene off sends the original pipeline to MongoDB.

```
Query path
  Application ──[$vectorSearch, ...rest]──▶ Madelene.Mongo seam ──request──▶ Madelene engine
                                                   │                         filter → plan → scan codes
                                                   │                         → rescore → merge buffer
                                                   ◀──────── top-k (_id, score) ─┘
                                                   │
                                                   └──[$match {_id: {$in}}, $addFields score, $sort, ...rest]──▶ MongoDB

Write path
  Application ──writes──▶ MongoDB ──change stream (updateLookup, projected)──▶ MongoIndexSync
                                                                               │ upsert / delete,
                                                                               ▼ resume token as checkpoint
                                                                          VectorIndex (buffer → segments)
```

* Madelene is an in-process library. There is no extra database server.
* MongoDB stays unchanged; Madelene reads its change stream and answers queries with ids.
* A filter Madelene can't evaluate throws `FilterNotSupportedException`, and the caller runs the original
  pipeline on MongoDB.

## 3. Projects

| Project | Responsibility | Dependencies |
|---|---|---|
| `src/Madelene.Core` | Engine: segments, quantization, IVF, filters, search, compaction, retention, multi-index engine | none |
| `src/Madelene.Mongo` | BSON mapping, `$vectorSearch` parsing, pipeline rewrite, change-stream sync with checkpoints and auto-resync | MongoDB.Driver |
| `tests/Madelene.Tests` | Recall, filter semantics against a reference evaluator (property test), persistence, crash and corruption cases, planner, MongoDB integration (opt-in) | xunit |
| `tools/Madelene.Bench` | Synthetic CLIP-like data or real `.npy` vectors; streaming ingest, ground truth, latency, load tests | none |

## 4. Data model

* **Index**: one vector path with dimensions and similarity, a list of filter paths, an optional time path.
  Defined with the same JSON as a MongoDB vector search index plus a `madelene` options block. In a
  multi-tenant host, one index per tenant gives physical tenant isolation.
* **Document**: an opaque id, a vector, filter values (multi-valued for arrays), an optional time.
* **Filter values** keep MongoDB's type brackets: numbers of any width compare equal by value; strings,
  booleans, ObjectIds, dates and UUIDs never compare equal across types.
* **Time** is Unix milliseconds everywhere (BSON's own resolution). Ticks would exceed 2^53 and lose
  exactness in the double-based filter values; milliseconds (~1.8e12) are exact.

## 5. Storage

Each index is a directory of immutable segments plus a small manifest. Segments are memory mapped, so RAM holds
only what queries touch, and the operating system's page cache decides the rest.

```
<root>/<index name, %-escaped>~<instance id>/
  index.json          definition (mongot-style + options)
  rotation.f32        seeded random orthogonal rotation (d × d), fixed per index
  manifest.json       live segments + tombstone generations + host checkpoint (temp file + atomic rename)
  seg-00000042/       one immutable segment, inside one time bucket
    meta.json           rows, clusters, time range, bucket, format version
    centroids.f32       IVF centroids (one row for flat segments: the segment mean)
    clusters.i32        row ranges per cluster (rows are stored grouped by cluster)
    codes.u8            1-bit codes, d/8 bytes per row
    factors.f32         per-row correction factor for the 1-bit estimate
    vectors.i8          int8 vectors for rescoring, d bytes per row
    scales.f32          per-row int8 scale
    time.i64            time column (Unix ms; MinValue = missing)
    ids.dat / ids.off   ids (UTF-8) and offsets
    idhash.u64 / .i32   sorted id hashes → rows (upsert/delete lookup)
    bloom.u64           bloom filter over ids (10 bits/id) so brand-new ids skip lookups
    terms.dat / .off    sorted term dictionary ("path\x1f<type>:<value>")
    post.off/.cnt/.kind posting list directory
    postings.bin        posting lists: sorted int32 rows, or a bitset when dense (> 1/32 of rows)
    tomb-000017.u64     tombstone bitset, generation 17
```

### Where the bytes go

Per vector at d = 768 with 18 tokens, measured on a 1M-row IVF segment of the 100M run:

| Part | Bytes | Where it is read |
|---|---|---|
| int8 vector + scale | 772 | only the rows a query rescores (a few thousand) |
| 1-bit code + factor | 100 | every row a query scans |
| posting lists | ~45 | every query with a token or source filter |
| id, id hash, bloom | ~27 | upserts, deletes, result ids |
| time | 8 | time filters |
| **total** | **~952** | ≈ 2.9 TB for 3B vectors, 0.95 TB for 1B |

The int8 copy is 81 % of the disk, and it's the part a query barely touches, so it can sit on SSD while the codes
stay in the page cache.

## 6. Vector encoding

* **Rotation.** Every vector (and query) is multiplied by one random orthogonal matrix, fixed per index. That
  spreads energy evenly over dimensions, which is what makes 1-bit codes unbiased enough to rank with.
* **1-bit codes (RaBitQ-style).** A row stores the signs of its rotated residual from its IVF centroid (96 B at
  768-d) plus one correction factor. The estimated similarity is `⟨c, q⟩ + f · (2·S − Q)`, where `S` is the sum of
  the query's rotated components at the set bits. `S` is computed with 96 lookup tables of 256 entries (one per
  code byte), built once per query.
* **int8 rescoring.** Each row also stores a symmetric int8 quantization of the (normalized) vector with a
  per-row scale. The best 1-bit candidates are rescored with it. `exact: true` scores every matching row with it.
* **IVF.** Segments with at least `ivfMinRows` rows get spherical k-means centroids (2·√n lists, trained on a
  sample of up to 128 rows per list, at most 500k) and store their rows grouped by list.

## 7. Query path

Every query pins one consistent snapshot of segments and the write buffer, so concurrent writes, seals and
compactions never change its answer midway.

1. **Validate** like MongoDB does: `limit ≤ numCandidates ≤ 10000`, `exact` excludes `numCandidates`, filter paths
   must be indexed. Unsupported filters throw `FilterNotSupportedException`.
2. **Prepare** the query: normalize (cosine), rotate once, build the 96 × 256 lookup tables.
3. **Filter**, segments in parallel: skip segments outside the time range implied by the filter (segment
   min/max time); evaluate the filter into a row bitset from posting lists and the time column; subtract
   tombstones.
4. **Size the pool**: the number of 1-bit candidates to rescore is `max(numCandidates, minRescorePool,
   rescorePoolFraction × matched rows)`, capped at `max(10,000, rescorePoolPerLimit × limit)`.
5. **Plan**:
   * Exact queries, single-list segments and segments where at most `exactScanThreshold` rows match are scanned
     whole (only the matching rows are scored).
   * The other segments share **one probe budget**. The query's similarity to every centroid is computed (per
     segment, in parallel), each segment's lists are sorted best first, and a heap merges them across segments.
     Lists are taken best first until they hold `probeCandidateFactor × pool` matching rows (exact counts from
     the row bitset) and at least the floor of `probeFraction × mean lists per segment × √(IVF segments)` lists.
6. **Scan codes** for the chosen rows in parallel across cores, keeping the best `pool` estimates.
7. **Rescore** the pool with int8 vectors.
8. **Merge** with an exact scan of the in-memory write buffer, drop duplicate ids (the buffer copy is newer and
   wins), return the top `limit` with score `(1 + similarity) / 2`, MongoDB's normalization.

### Why one shared probe budget

Up to version 0.1 each segment was planned as if it held all of the query's matches: it probed lists until *it*
alone had `probeCandidateFactor × pool` matching rows. With 100 daily segments that read about 9 % of every
segment, and the work grew linearly with the number of segments: at 1B rows (1,000 segments of 1M) a broad query
would have scanned about 90M codes. Ranking lists across segments instead puts the budget where the query's
neighbours are. The scan then grows with the pool (and with √segments through the floor), not with the data.
The 100M measurements before and after the change are in [Performance](#12-performance).

### Filter support

The MongoDB `$vectorSearch` filter subset, with MongoDB's multikey array and type-bracket semantics: `$and`,
`$or`, `$nor`, `$not`, `$eq`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`, `$lt`, `$lte`. A property test checks the
segment evaluator against a reference evaluator over random filters. Rejected with a clear error, so the caller
can fall back to MongoDB: null equality, `$exists`, `$regex`, whole-array equality, unindexed paths.

## 8. Write path

* **Upsert**: tombstone any previous copy (bloom filter, then hash lookup, per segment), then append to the
  write buffer. Readers de-duplicate the brief window where both are visible (the buffer copy wins).
* **Insert**: the same without the per-segment lookup, for ids known to be new (the MongoDB backfill uses it).
  The lookup costs one bloom probe per segment, which matters once there are hundreds of segments.
* **Seal**: when the buffer reaches `sealRows` (or on `Flush`), its rows are grouped by time bucket and each
  group becomes a segment, built under a temporary directory name, fsynced, then renamed.
* **Compaction** merges a bucket once it has `mergeFactor` segments, or rewrites a segment with more than
  `tombstoneRewriteRatio` deleted rows. It reads the live rows as int8 vectors (about 1 KB per row in memory, not
  3 KB of floats) and builds outside the write lock, decoding each row into per-thread buffers; only the k-means
  sample is held as floats. Under the lock it re-applies deletes that happened meanwhile and swaps the segments.
  Merged segments past `ivfMinRows` get an IVF layer. After each merge a compacting GC returns the merge's memory
  to the OS (see [Memory](#11-memory)).
* **Retention**: `DeleteOlderThan` drops whole segments whose newest row is older than the cutoff, without
  reading them, and tombstones the old rows of straddling segments.

Lifecycle of a row: write buffer (float32, searchable at once) → sealed segment (flat, 250k rows) → merged segment
(IVF, 1M rows per daily bucket by default) → tombstoned on delete → rewritten when 20 % of its segment is deleted
→ dropped with its bucket by retention.

## 9. Durability and recovery

* **No write-ahead log.** The host's change feed is the log. After a restart the host replays from
  `VectorIndex.Checkpoint`; every operation is idempotent (upsert by id, delete by id), so replaying more than
  necessary is harmless. Writes after the last flush are lost on a crash and come back with the replay.
* **Flush(checkpoint)**: seal, write new tombstone generations, write the manifest with the host's checkpoint,
  then delete superseded tombstone files. The previous generation stays on disk until the manifest that stops
  referencing it is durable. Every segment, tombstone and manifest file is fsynced before it is renamed into
  place. If a seal fails (e.g. disk full) the buffer is restored, so a later checkpoint can never skip rows that
  were not written.
* Unreferenced segment directories and tombstone files are garbage-collected at open.
* Each index instance has its own directory, so dropping and re-creating an index never touches files that an
  in-flight query or compaction still has memory mapped (Windows can neither delete nor rename them). A drop
  writes a durable `DROPPED` marker, deletes what it can (marker last) and leaves the rest for the next start.
* An index that fails to open (corrupt or truncated files, an old format) is reported in
  `MadeleneEngine.FailedIndexes` instead of failing the engine; drop it and rebuild it from MongoDB. Segment
  files are checked against their row counts at open, and a referenced tombstone file that is missing or short is
  an error, never "no deletes".

## 10. MongoDB integration

`MongoIndexSync`:

1. No checkpoint: read the cluster's `operationTime`, scan the collection (projected to the indexed paths) with
   `Insert`, flush with `ts:<operationTime>` as the checkpoint.
2. Tail a change stream (`fullDocument: updateLookup`, projected) from the checkpoint. Inserts, updates and
   replaces become upserts (or deletes when the document no longer matches `Match` or lost its vector); deletes
   become deletes. Every N changes or T seconds, flush with the resume token. Empty batches still advance the
   token, so idle collections don't fall off the oplog.
3. If the checkpoint is no longer in the oplog (errors 286/280), or the collection is dropped or renamed, and
   `AutoResync` is on, drop and rebuild the index (at most 3 times in a row, so an oplog window shorter than a
   full scan fails loudly instead of looping). An interrupted backfill (segments but no checkpoint) restarts
   from an empty index.
4. Documents whose vector is missing, the wrong size, NaN or all zeros are left out of the index instead of
   stalling the stream.

`MongoVectorSearch.RewritePipeline` turns `[ {$vectorSearch}, ...rest ]` into `[ {$match: {_id: {$in}}},
{$addFields: {score}}, {$sort}, ...rest ]` and rewrites `{$meta: "vectorSearchScore"}` references, so pipelines
after the first stage run unchanged.

## 11. Memory

| What | Size | Evictable |
|---|---|---|
| 1-bit codes + factors | 100 B/vector | yes (page cache) |
| int8 vectors, ids, postings, time | ~850 B/vector | yes (page cache) |
| Centroids, term dictionaries, tombstones, segment metadata | ~7–10 B/vector | no (managed heap) |
| Write buffer | `sealRows` × 3 KB at 768-d (0.75 GB at the default) | no |
| A running merge | ~1 KB per merged row + the k-means sample (≤ 500k rows × 3 KB) | released after the merge |

At 3B vectors the non-evictable part is ~21–30 GB, inside the 43 B/vector budget. Everything else is page cache:
the more of the codes it holds, the faster unfiltered queries are.

**Memory after compaction.** .NET's Server GC keeps freed memory committed for reuse. A merge allocates a lot and
drops it all at the end, so a process that compacts once a day would hold that memory permanently: in a 10M
benchmark under a 24 GB container limit, a process held ~11 GB of anonymous memory after its daily compactions
while its live heap was 0.14 GB. Since 0.2, compaction keeps rows as int8 instead of floats (about a third of the
memory per row) and, after each merge, runs a blocking, compacting, aggressive collection that decommits the
free memory (`releaseMemoryAfterCompaction`, on by default). The live heap is small at that point, so the pause
is short. Measurements are in [Performance](#12-performance).

## 12. Performance

All numbers are on synthetic CLIP-like data: 1,000 clusters around a shared direction, with "text" queries
shifted by a modality gap so the best matches have low cosine and thin margins. One tenant, 200 sources (Zipf),
18 tokens per row from a 500-word vocabulary. 768-d, limit 24 / `numCandidates` 360 unless noted. Recall is
against exact float32 search over the rows that match the filter. Machine: 24-core Windows 11 desktop, 64 GB RAM,
local NVMe.

### 100M vectors: 0.1 against 0.2

100 daily IVF segments of 1M rows (2,000 lists each), 95 GB on disk, larger than the 64 GB of RAM. The 0.1
engine built the index and the 0.2 engine reused it (the 0.2 changes are all at query and compaction time; the
format is unchanged), with the same queries and ground truth. Both runs used the 0.2 benchmark tool, which is why
the 0.1 report also logs memory after compaction; the 0.2 run compacted nothing, so its memory line is taken right
after opening the index. Reports: `bench-results/bench-stream-100000000x768-20261126-034435-v0.1-baseline.json`
and `...-20261126-034637.json`.

Repeat pass, limit 24 / `numCandidates` 360:

| Filter | Selectivity | Recall@24 (both) | p50 0.1 | p50 0.2 | p99 0.1 | p99 0.2 | Rows scored 0.1 | Rows scored 0.2 |
|---|---|---|---|---|---|---|---|---|
| tenant only (all 100 days) | 100 % | 0.951 | 68.8 ms | 17.4 ms | 93.3 ms | 38.7 ms | 10.3M | 568k |
| + busiest source | 21.3 % | 0.983 | 108.3 ms | 17.3 ms | 133.1 ms | 49.2 ms | 9.2M | 121k |
| + mid source | 0.28 % | 0.984 | 49.6 ms | 21.3 ms | 66.7 ms | 45.6 ms | 281k | 281k |
| + 2 tokens (AND) | 28.4 % | 0.970 | 102.7 ms | 17.8 ms | 133.3 ms | 66.1 ms | 9.4M | 161k |
| + token OR + NOT | 20.8 % | 0.972 | 120.1 ms | 18.7 ms | 149.4 ms | 56.0 ms | 9.2M | 118k |
| + last 20 % of time | 20.0 % | 0.978 | 39.9 ms | 11.9 ms | 77.6 ms | 23.3 ms | 2.1M | 228k |
| + mid source + 5 % of time | 0.014 % | 0.993 | 13.8 ms | 4.7 ms | 22.4 ms | 15.5 ms | 14k | 14k |

Recall@24 is identical to the third decimal on every filter: 0.1 scanned 18–80× more rows than it needed, and the
misses of both versions come from int8 ranking near-ties, not from the scan. First-pass (colder) p50 went from
21–141 ms to 5–25 ms.

Limit 360 / `numCandidates` 5,400 (repeat pass): recall@360 0.854 → **0.957** on "tenant only" (the rescore pool
is now capped at 64 × limit = 23,040 instead of 10,000), 0.967–0.994 → 0.984–0.994 on the other filters; p50 from
17–136 ms to 7–34 ms.

Where the time goes (repeat pass, tenant + busiest source): planning 36.7 → 9.2 ms (centroid scoring in parallel
across segments), code scan 64.3 → 4.9 ms. The narrow source filter is scanned flat in both versions (≤ 100k
matches per segment); its planning still scores every centroid of the 100 segments, which is most of its 21 ms.

Concurrent clients, a mix of the seven filters, 20 s per level:

| Clients | QPS 0.1 | QPS 0.2 | p50 0.1 | p50 0.2 | p99 0.1 | p99 0.2 |
|---|---|---|---|---|---|---|
| 1 | 13.5 | 53.9 | 70 ms | 18 ms | 136 ms | 51 ms |
| 4 | 24.9 | 96.5 | 168 ms | 40 ms | 309 ms | 79 ms |
| 16 | 27.8 | 111.6 | 577 ms | 141 ms | 1,212 ms | 271 ms |

### Memory after compaction: 0.1 against 0.2

Private (non-shareable) memory of the process right after compaction, before anything else runs. Server GC, Windows,
no container limit.

| Run | 0.1 | 0.2 |
|---|---|---|
| 10M rows: 40 sealed segments merged into 10 IVF segments | 8.72 GB private, 8.00 GB committed by the GC | **0.95 GB** private, 0.50 GB committed |
| 100M rows: 400 → 100 segments | 11.53 GB private, 10.72 GB committed | not rebuilt in 0.2 (the 10M pair is the comparison) |

The live heap after compaction is 0.07 GB at 10M and ~0.65 GB at 100M in both versions; 0.1 kept the rest committed.
Both 10M builds ran while the 100M build was compacting, so their compaction times (13.6 min for 0.1, 14.8 min
for 0.2) are inflated alike; uncontended, a 1M-row merge takes ~36 s on 24 cores. Disk is unchanged at 951–952
B/vector.

### What this means at 1B

Projected from the 100M measurements, not measured:

* The scan no longer grows linearly with segments. Lists are taken until 8 × the pool of matching rows is
  reached, with a floor of 3 % × 2,000 × √segments lists: 600 lists at 100 segments (568k rows scored for an
  unfiltered query; the best-ranked lists are larger than average), ~1,900 lists at 1,000 segments of 1M
  rows, so roughly 1.8M rows: about 3× the 100M scan, where 0.1 would have been 10×.
* Planning becomes the cost to watch. It scores every centroid of every searched segment: total lists are
  2·√(rows × segments), 3 KB each at 768-d. 1B rows in 1,000 segments of 1M is 2M centroids (6.1 GB, read
  once per query: ~90 ms at the ~67 GB/s planning reached here, 614 MB in 9 ms). In 100 segments of 10M it is
  0.63M centroids (1.9 GB, ~30 ms). Prefer fewer, larger segments, and use time filters where queries
  allow. A two-level centroid index (cluster the centroids, score only the nearest groups) is the next step if
  all-time queries at that scale must stay under 100 ms.
* Queries with a time range only plan and scan the segments in range, so their cost follows the range, not the
  total.


### 1M vectors, one IVF segment (2,000 lists), fully cached

| Filter | Selectivity | Recall@24 | p50 | p99 |
|---|---|---|---|---|
| tenant only | 100% | 0.984 | 1.7 ms | 17.1 ms |
| + busiest source | 21.3% | 0.987 | 1.0 ms | 1.4 ms |
| + mid source | 0.27% | 0.988 | 0.9 ms | 6.7 ms |
| + 2 tokens (AND) | 28.2% | 0.990 | 1.0 ms | 1.4 ms |
| + token OR + NOT | 20.8% | 0.985 | 1.1 ms | 2.0 ms |
| + last 20% of time | 20.0% | 0.992 | 4.5 ms | 7.7 ms |
| + mid source + 5% of time | 0.016% | 0.995 | 11.9 ms | 15.3 ms |

Ingest ~91k vectors/s; compaction + IVF build of 1M rows 36 s; disk 950 B/vector.

## 13. Alternatives that were measured

The same harness, data, filters and ground truth were run against other engines behind the same
`$vectorSearch` contract, all against Madelene 0.1 (before the 0.2 query and memory changes). Full reports are in
[benchmarks/](benchmarks/README.md).

| Engine | Result against the 3B / 128 GB budget |
|---|---|
| Milvus 3.0 (HNSW, IVF, RaBitQ, DiskANN, AiSAQ) | 0.26–1.24 KB of RAM per vector in every configuration; 2–25× slower per query at the same recall at 10M; ~7× the disk. Doesn't fit. |
| Qdrant 1.20 (HNSW + int8 / binary quantization, ACORN) | Reaches the recall target with int8 but at 17–150× Madelene's non-evictable RAM; the only near-budget layout (1-bit, nothing pinned) missed recall on 5 of 7 filters at 100M and was 7–55× slower than Madelene 0.1. Doesn't fit. |
| SPTAG / SPANN and SPFresh (the LIRE update protocol, file backend) | No filter support (all filtering had to be written around it); recall plateaus at ~0.95 on broad filters; SPFresh stays fresh under churn but writes 16–40× slower and its posting file grows without bound; ≥ 56 B/vector of pinned RAM at 3B. Doesn't fit. |

One idea worth borrowing from SPFresh: local rebalancing (split an oversized IVF list and reassign only its
neighbours) instead of rewriting whole segments on compaction.

A separate branch added non-exact filters (prefix and general `$regex` over the term dictionary, a `$fuzzy`
operator), BM25 full-text search and hybrid rank fusion; its measurements are in
[benchmarks/fuzzy-and-text.md](benchmarks/fuzzy-and-text.md). That code is not part of this version.

## 14. Known gaps

* Upserts check every segment's bloom filter; at thousands of segments that dominates ingest. Inserts known to
  be new (`Insert`) skip it. A next step is a per-bucket id range filter, or coupling checkpoints to seals so
  change-stream inserts can always use `Insert`.
* Seals and compactions run on the writer thread / caller thread and block writes while a segment is built
  (seconds per 250k rows). A background builder is the next step.
* Compaction rewrites a whole bucket when updates touch it. With updates spread over every day (vectors or
  tags changed in place), every bucket is rewritten daily. Local list splits (as in SPFresh) would cut that.
* Merges re-encode from int8 vectors (no float32 copy is kept); quality loss is small but compounds if a row is
  merged many times. An optional float16 column would remove it.
* Planning scores every centroid of every searched segment. At 100 segments that is 9 ms; at thousands of
  segments it would dominate all-time queries (see [What this means at 1B](#what-this-means-at-1b)). A two-level
  centroid index, or float16/int8 centroids (2–4× less memory bandwidth), is the next step.
* Unfiltered queries over many segments read the probed lists of every segment; once the codes don't fit in the
  page cache they are disk-bound. A small global coarse index over segment centroids, or tiered (older, larger)
  segments, would reduce the lists touched.
* Filter values are doubles: int64 values above 2^53 and Decimal128 lose exactness. Positional paths (`a.0.b`)
  are not traversed. The time field must be a BSON date (the first one is used if it is an array).
* `exact: true` scores with int8 vectors, not float32, so it is "exact over int8".
* Retention (`DeleteOlderThan`) is not driven by the change feed; it is a host-scheduled job.
* No fsync of directory entries after renames (not exposed by .NET on Windows); NTFS journaling covers the rename
  itself.
* No null/missing equality, `$exists`, whole-array equality, or range filters on non-time arrays with a combined
  lower and upper bound per element (each bound is evaluated per MongoDB semantics).
* Euclidean similarity is not supported (cosine and dotProduct are).
* Single process; no replication. The index is derived, so recovery is a rebuild from MongoDB.
* Benchmarks are synthetic. Recall on real CLIP text-to-image queries must be measured with the bench's `.npy`
  input before relying on the defaults.
