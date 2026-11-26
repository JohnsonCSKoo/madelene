# Changelog

How Madelene got here, iteration by iteration: what changed, why, and what was measured. Benchmarks are on
synthetic CLIP-like data on one 24-core desktop with 64 GB RAM and NVMe unless noted. Reports are in
`bench-results/` and [docs/benchmarks/](docs/benchmarks/README.md).

## 0.2.0: query cost that doesn't grow with segments, and memory that goes back after compaction

Fixes for the costs the 100M benchmark and the SPTAG/SPFresh comparison found.

* **One probe budget across segments.** The IVF planner gave every segment the whole query's budget, so a broad
  query read ~9 % of every segment and its cost grew linearly with the segment count. Lists are now ranked by
  centroid similarity across all searched segments (a heap merge of each segment's best-first order) and taken
  until they hold `probeCandidateFactor` × pool matching rows, counted exactly from the filter bitset, with a floor
  of `probeFraction` × mean lists per segment × √segments. For a single segment this is the old plan, with exact
  match counts in place of a selectivity estimate.
* **Parallel filter and planning.** Filter evaluation and centroid scoring run per segment in parallel instead of
  one segment after another.
* **Rescore pool for large limits.** The pool was capped at 10,000 (MongoDB's `numCandidates` limit) whatever the
  `limit`; it's now capped at `max(10,000, rescorePoolPerLimit × limit)` (new option, default 64).
* **Leaner merges.** Compaction carries rows as int8 (≈ 1 KB per row with id and terms) instead of decoded floats
  (≈ 3 KB of floats, plus id and terms), and the segment builder decodes each row into per-thread buffers;
  only the k-means sample is materialized.
* **Memory returned after compaction.** After each merge, a blocking, compacting, aggressive GC decommits what
  the merge used (`releaseMemoryAfterCompaction`, new option, default on).
* Benchmark: logs process memory (private bytes, GC committed, GC heap) after compaction and after the queries.
* Docs: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) (replaces `docs/DESIGN.md`), [docs/GUIDE.md](docs/GUIDE.md),
  this changelog, [docs/benchmarks/](docs/benchmarks/README.md). The README's example passed `DateTime.Ticks` as
  the time; it now uses `FieldValue.ToUnixMs`.
* 4 new tests (45 in total): the shared budget scans < 25 % of rows across 8 IVF segments at recall ≥ 0.90, a
  selective filter keeps recall under the shared budget, the pool cap follows the limit, and int8 merges keep exact
  results.

Measured on the same 100M index (100 daily segments, 95 GB, larger than RAM), same queries and ground truth
([details](docs/ARCHITECTURE.md#100m-vectors-01-against-02)):

| | 0.1 | 0.2 |
|---|---|---|
| p50, repeat pass, seven filters (limit 24) | 13.8–120.1 ms | 4.7–21.3 ms |
| Recall@24 | 0.951–0.993 | identical |
| Rows scored, broad filters | 2.1M–10.3M | 118k–568k |
| Recall@360, tenant only | 0.854 | 0.957 |
| Throughput, 1 / 4 / 16 clients | 13.5 / 24.9 / 27.8 QPS | 53.9 / 96.5 / 111.6 QPS |
| p99 at 16 clients | 1,212 ms | 271 ms |
| Private memory right after compacting 10M rows | 8.72 GB | 0.95 GB |

Not changed: the segment format (0.1 indexes open as they are), disk per vector (952 B), recall at limit 24.


## 0.1.1: open-source preparation

* Apache-2.0 `LICENSE` (full text) and a `NOTICE` file.
* Benchmark reports no longer record local file paths (`--dir`, `--vectors`, `--query-vectors` are left out of
  the JSON), and their logs use UTC times.

No engine change; the 41 tests passed unchanged.

## Experiments (separate branches, not merged)

All four were measured against Madelene 0.1, before the 0.2 changes.

Each experiment ran the same harness, data, filters and ground truth as the engine's own benchmarks, behind the
same `$vectorSearch` contract (`IVectorSearchBackend`). The reports are included in
[docs/benchmarks/](docs/benchmarks/README.md); the backend code is not part of this package.

* **Milvus 3.0** ([report](docs/benchmarks/comparison-milvus.md)), 10M rows: every index type needed
  0.26–1.24 KB of RAM per vector (3B vectors: 0.8–3.7 TB against a 128 GB budget). At the same recall, Milvus was
  2–25× slower per query and reached 16–35 QPS against Madelene's 432, with ~7× the disk. 100M was not run: it
  doesn't fit the memory the machine could give it.
* **Qdrant 1.20** ([report](docs/benchmarks/comparison-qdrant.md)), 10M and 100M rows: int8 quantization reached
  the recall target but pinned 17–150× Madelene's RAM per vector; filtered recall needed ACORN. At 100M, the only
  layout near the RAM budget (1-bit codes, nothing pinned) missed recall@24 ≥ 0.95 on five of seven filters and
  was 7–55× slower.
* **SPTAG/SPANN and SPFresh** ([report](docs/benchmarks/comparison-sptag-spfresh.md)), 10M rows, static and
  four simulated days of churn, every engine under the same 24 GB container limit: SPANN has no filters, so
  filtering was written around it; recall plateaued at ~0.95 on broad filters (Madelene 0.977–0.999). SPFresh
  stayed fresh under churn without rebuilds but wrote 16–40× slower and its posting file kept growing; static
  SPANN was stale until a near-full nightly rebuild. Both need ≥ 56 B/vector of pinned RAM at 3B. This run also
  found that Madelene kept ~11 GB of memory committed after daily compactions (fixed in 0.2).
* **Non-exact filters and full-text** ([report](docs/benchmarks/fuzzy-and-text.md)), 10M rows: prefix and general
  `$regex` over the term dictionary, a `$fuzzy` operator, BM25 full-text search (`$search`) with collection-wide
  statistics, and hybrid rank fusion. BM25 scores matched MongoDB Atlas Search to 3 decimals; the text index
  added 26 B/vector on disk.

## 0.1.0: prototype

The first version of the engine and its MongoDB integration.

* Time-bucketed immutable segments on SSD, memory mapped: 1-bit RaBitQ-style codes for scanning, int8 vectors
  for rescoring, posting lists per filter value, a time column, an IVF layer (spherical k-means, 2·√n lists) on
  large segments. An in-memory write buffer searched exactly; tombstones; compaction; retention by time.
* MongoDB `$vectorSearch` surface: the filter subset with multikey and type-bracket semantics, `exact`, score
  normalization; a property test against a reference evaluator.
* `Madelene.Mongo`: backfill, change-stream sync with resume-token checkpoints and auto-resync, pipeline rewrite.
* Durability without a WAL (the change stream is the log), fsync + atomic renames, corruption detection at open.
* Benchmarks: 1M rows in one segment, 1–12 ms p50 at recall@24 0.984–0.995. 100M rows in 100 daily segments
  (95 GB, larger than RAM), 13–117 ms p50 at recall@24 0.95–0.99, 952 B/vector on disk, 0.7 GB managed heap.
  The 100M run showed the planner's per-segment probe budget and serial centroid ranking as the main costs.
