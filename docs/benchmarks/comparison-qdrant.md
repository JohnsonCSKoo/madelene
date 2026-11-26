# Madelene vs Qdrant

> Measured on a separate experimental branch of Madelene (the Qdrant work). The backend code, deployment files and raw result files it mentions are not part of this package; this report is included for its findings. The Madelene figures in it are version 0.1, and its command lines use options of that branch's benchmark tool that the one in this repo doesn't have. See [the benchmark index](README.md).


Same data, queries, ground truth and harness (`tools/Madelene.Bench --stream`, `--backend qdrant`) against
Qdrant 1.20.0 through `src/Madelene.Qdrant`, which serves the same `$vectorSearch` contract
(`IVectorSearchBackend`) as the embedded engine.

**Short version:** Qdrant reaches the recall target with int8 quantization, is faster than Madelene on
unfiltered and very narrow filters once warm, and slower on mid-selectivity filters, at `numCandidates`
5,400, and under concurrent load. It needs 4.4–5.1× Madelene's disk and, depending on the layout,
17–150× its non-evictable RAM per vector. At 100M, the only Qdrant layout that comes near the RAM budget
(1-bit codes, nothing pinned, float16 originals) misses recall@24 ≥ 0.95 on five of seven filters and is
7–55× slower than Madelene 0.1, with 2.7× the disk. No Qdrant layout measured here fits 3 B vectors in
128 GB of RAM on one node. See [Verdict](#verdict).

## Setup

* Data: synthetic CLIP-like 768-d vectors (`RowGenerator`, seed 7), one tenant, 200 sources, 18 tokens per
  row from a 500-word vocabulary, rows spread evenly over the days and ingested in time order.
* Queries: 50 "text" probes per filter, the seven filters below, `limit` 24 / `numCandidates` 360 and
  `limit` 360 / `numCandidates` 5,400, a first and a repeat pass, then a load test mixing all seven filters
  at limit 24 for 20 s each with 1, 4 and 16 clients. Recall is against exact float32 search over the rows
  matching the filter (the same cached truth file for both engines).
* Machine: 24 cores, 64 GB RAM, local NVMe, Windows 11. Madelene runs in the bench process. Qdrant runs
  in Docker (WSL2 VM with 31 GB), container `madelene-qdrant` limited to 24 GB, queried over gRPC from the
  bench on the host. gRPC adds roughly 0.3–0.5 ms per query to Qdrant's numbers.
* Qdrant settings common to every configuration: one collection, cosine distance, HNSW `m` 16 /
  `ef_construct` 100, original float32 vectors memory-mapped (`on_disk`), payload on disk, keyword
  payload indexes on the three filter paths (`is_tenant` on the tenant field), an integer index on the
  time field (`is_principal`), ACORN on (`max_selectivity` 0.4), `hnsw_ef` = `numCandidates`, quantized
  search with rescoring by the originals. Rows go in with 8 parallel upserts of 256 points (`wait`).
  Collections were bulk-loaded (HNSW indexing off until all rows were in, then on).

Qdrant configurations (RAM is what Qdrant pins, the rest is memory-mapped):

| Name | Quantized vectors | HNSW graph | Payload indexes | Oversampling |
|---|---|---|---|---|
| int8 | int8 (768 B), pinned | RAM | RAM | 2 |
| bq2 | 2-bit (192 B), pinned | mmap | RAM | 10 |
| bq1-disk | 1-bit (96 B) with 8-bit query encoding, pinned | mmap | mmap | 10 |

ACORN was required. Without it, at 1M rows, recall@24 was 0.497 on "last 20% of time" and 0.833 on the
busiest source; with it, ≥ 0.993 on every filter at 1M.

Each Qdrant configuration was measured with only its own collection loaded and after a container restart
(Qdrant loads every collection in its storage at startup, so earlier runs with several collections present
overstated RAM; those reports are not used here). "First" is therefore a cold pass.

## Results at 10M (10 days)

### Recall and latency, limit 24 / numCandidates 360

p50 first / p50 repeat / p99 repeat, in ms.

| Filter | Selectivity | Madelene R@24 | Madelene | int8 R@24 | int8 | bq2 R@24 | bq2 | bq1-disk R@24 | bq1-disk |
|---|---|---|---|---|---|---|---|---|---|
| tenant only | 100% | 0.977 | 16.4 / 15.5 / 33.4 | 0.986 | 13.3 / 3.1 / 3.6 | 0.947 | 44.5 / 3.1 / 3.8 | 0.952 | 40.2 / 4.5 / 5.6 |
| + busiest source | 21.3% | 0.983 | 7.7 / 7.8 / 15.1 | 0.990 | 18.4 / 11.9 / 13.5 | 0.984 | 27.0 / 13.8 / 15.8 | 0.981 | 34.6 / 25.5 / 30.6 |
| + mid source | 0.28% | 0.994 | 6.5 / 6.2 / 27.8 | 1.000 | 11.4 / 3.0 / 3.3 | 0.998 | 10.7 / 2.7 / 3.3 | 1.000 | 10.2 / 3.7 / 4.7 |
| + 2 tokens (AND) | 28.4% | 0.983 | 8.9 / 9.0 / 32.6 | 0.996 | 32.7 / 28.2 / 33.0 | 0.978 | 40.1 / 33.1 / 43.4 | 0.983 | 50.9 / 43.7 / 55.4 |
| + token OR + NOT | 20.8% | 0.978 | 9.6 / 9.1 / 30.6 | 0.993 | 37.0 / 32.3 / 41.9 | 0.985 | 46.8 / 40.8 / 48.5 | 0.980 | 55.6 / 50.3 / 56.4 |
| + last 20% of time | 20.0% | 0.981 | 4.9 / 4.5 / 5.6 | 0.953 | 16.2 / 6.9 / 8.3 | **0.911** | 38.7 / 7.7 / 9.5 | **0.886** | 36.3 / 9.9 / 12.4 |
| + mid source + 5% of time | 0.014% | 0.993 | 2.3 / 2.5 / 19.8 | 1.000 | 4.9 / 1.8 / 3.1 | 1.000 | 4.7 / 2.2 / 2.7 | 1.000 | 5.0 / 2.6 / 7.0 |

* int8 meets recall@24 ≥ 0.95 on every filter (lowest 0.953). bq2 and bq1-disk do not: "last 20% of
  time" stays at 0.91 / 0.89 even at oversampling 10 (bq2 at oversampling 4: 0.73), and "tenant only" is
  at the edge (0.947 / 0.952).
* Warm, Qdrant is 1.4–5× faster than Madelene on the unfiltered and narrow filters (HNSW visits a few
  hundred nodes; Madelene scans IVF lists). On the 20–28% filters Madelene is 1.5–5.5× faster: ACORN
  explores neighbours-of-neighbours to stay inside the filter, and the binary configurations pay for
  reading the mmap'd graph and originals.
* Cold (after a restart), Qdrant's first pass is 5–55 ms; Madelene's first pass is close to its repeat pass.

### limit 360 / numCandidates 5,400

| Filter | Madelene R@360 | p50 | int8 R@360 | p50 | bq2 R@360 | p50 | bq1-disk R@360 | p50 |
|---|---|---|---|---|---|---|---|---|
| tenant only | 0.988 | 15.8 | 0.995 | 131 | 0.988 | 257 | 0.990 | 171 |
| + busiest source | 0.992 | 13.2 | 0.997 | 186 | 0.996 | 237 | 0.995 | 314 |
| + mid source | 0.989 | 8.7 | 1.000 | 43 | 1.000 | 51 | 1.000 | 42 |
| + 2 tokens (AND) | 0.990 | 12.7 | 0.998 | 338 | 0.994 | 439 | 0.996 | 454 |
| + token OR + NOT | 0.991 | 13.9 | 0.997 | 351 | 0.996 | 463 | 0.996 | 458 |
| + last 20% of time | 0.991 | 7.2 | 0.984 | 119 | 0.987 | 125 | 0.988 | 135 |
| + mid source + 5% of time | 0.995 | 2.9 | 1.000 | 11.0 | 1.000 | 10.6 | 1.000 | 11.6 |

HNSW with `ef` 5,400 is expensive: Qdrant is 4–35× slower here. Madelene's cost barely changes with
`numCandidates` because it scans a fixed share of IVF lists and only the rescore set grows.

Exact search (`exact: true`) over "last 20% of time" (2M rows): Madelene p50 29 ms / p99 64 ms (int8
vectors, recall 0.981); Qdrant p50 164–175 ms / p99 4.3–7.1 s (float32 originals from mmap, recall 1.0).

### Throughput (all seven filters mixed, limit 24)

| Clients | Madelene QPS | p50 / p99 | int8 QPS | p50 / p99 | bq2 QPS | p50 / p99 | bq1-disk QPS | p50 / p99 |
|---|---|---|---|---|---|---|---|---|
| 1 | 118 | 7.8 / 25 | 74 | 7.7 / 39 | 59 | 8.6 / 56 | 15 | 23 / 825 |
| 4 | 307 | 11.5 / 37 | 135 | 21.7 / 88 | 117 | 23.9 / 100 | 77 | 37 / 169 |
| 16 | 496 | 29.7 / 82 | 143 | 104 / 256 | 125 | 124 / 277 | 88 | 175 / 374 |

Qdrant saturates at about 4 clients. The bq1-disk single-client stage ran right after the cold restart
and was still faulting the graph and payload indexes in from disk.

## Operational footprint at 10M

| | Madelene | Qdrant int8 | Qdrant bq2 | Qdrant bq1-disk |
|---|---|---|---|---|
| Processes | in-process library | 1 container | 1 container | 1 container |
| Ingest | 94,500 rows/s (1.8 min) | 17,800 rows/s (9.4 min) | 16,400 rows/s (10.2 min) | 15,900 rows/s (10.5 min) |
| Searchable | immediately (buffer searched exactly) | immediately (unindexed segments are brute-forced) | same | same |
| Index build after ingest | compaction 6.2 min | 5.8 min | 7.9 min | 4.0 min |
| Fully indexed after | 8.0 min | 15.2 min | 18.1 min | 14.4 min |
| Disk | 9.5 GB = **951 B/vector** | 48.8 GB = **4,876 B/vector** | 43.0 GB = **4,301 B/vector** | 42.0 GB = **4,205 B/vector** |
| Non-evictable RAM after queries | heap 0.10 GB = **10 B/vector** | anon 14.5 GB = **1,448 B/vector** | anon 8.1 GB = **814 B/vector** | anon 1.6 GB = **165 B/vector** |
| Peak non-evictable RAM | — | 17.5 GB (bulk load) | 17.2 GB¹ | 9.8 GB (bulk load) |
| Page cache used | working set 4.5 GB (mapped index pages) | fills the 24 GB limit | fills the limit | fills the limit |

¹ measured with the int8 collection also loaded; the bq2 build was not repeated alone.

Where Qdrant's disk goes (1M breakdown, scales linearly): float32 originals 3,072 B, quantized copy
96–768 B, payload index ~560 B, payload ~260 B, HNSW ~44 B. Peak disk during the build was about 1.3×
the final size (bq1-disk: 55 GB peak for 42 GB final). Madelene stores int8 vectors plus 1-bit codes and
posting lists, and no float32 originals.

Anonymous ("anon") memory is what the kernel cannot evict: heap, pinned quantized vectors and pinned
indexes. During a bulk load Qdrant's anon memory grew about 1 GB per million rows (unindexed segments
keep their payload indexes in memory) and fell back after indexing.

### Loading without bulk mode, and pinning nothing (10M)

Bulk mode does not scale past the container's RAM (1 GB per million rows), so two more 10M runs let the
optimizer index while rows arrive (`--qdrant-bulk-load false`). `bq1-cold` is bq1-disk with the 1-bit
codes memory-mapped too: nothing per-vector is pinned.

| | bq1-disk, bulk | bq1-disk, no bulk | bq1-cold, no bulk |
|---|---|---|---|
| Ingest | 15,900 rows/s, then 4.0 min indexing | 10,200 rows/s (falling from 15k to 8.8k), 0.8 min to finish | 10,000 rows/s (falling to 8.7k), 0.9 min to finish |
| Peak anon during load | 9.8 GB | 3.1–5.4 GB | 5.3–5.9 GB |
| Anon after restart + queries | 1.6 GB = 165 B/vector | 1.6 GB = 163 B/vector | 0.7 GB = **69 B/vector** |
| Peak disk / final | 1.3× | 1.7× | 2.4× (103 GB / 42 GB) |
| R@24 tenant only / last 20% of time / others | 0.952 / 0.886 / ≥ 0.980 | 0.947 / **0.798** / ≥ 0.982 | 0.952 / **0.691** / ≥ 0.978 |
| Repeat p50, the seven filters | 2.6–50 ms | 2.4–52 ms | 1.8–47 ms |
| First (cold) p50 / worst p99 | 5–56 ms / 3.6 s | 5–59 ms / 4.0 s | 5–86 ms / 3.6 s |
| R@360 range, p50 | 0.988–1.0, 12–458 ms | 0.990–1.0, 12–441 ms | 0.947–1.0, 9–414 ms |
| QPS at 1 / 4 / 16 clients | 15 / 77 / 88 | 16 / 74 / 87 | 12 / 83 / 97 |

Indexing during the load costs a third of the ingest rate and, with ACORN, hurts the time-window filter:
segments built while rows arrive are smaller and are merged later, and "last 20% of time" fell from 0.886
to 0.80 and 0.69 at `numCandidates` 360. Memory-mapping the 1-bit codes changed warm latency very little
at 10M because the page cache (the rest of the 24 GB limit) still held most of the 42 GB collection. In
all three, the first query after a restart on a broad filter took up to 3.6–4.0 s.

## Results at 100M (100 days)

Compared with Madelene's existing 100M run (`bench-stream-100000000x768-20261111-123000.json`; same
options: `--stream --rows 100000000 --days 100 --bucket-days 1 --queries 50 --ivf-min-rows 500000`,
seed 7, same truth file).

Only one Qdrant configuration was measured at 100M: **bq1-cold with float16 originals, loaded without
bulk mode** (`--qdrant-config bq1-cold --qdrant-float16 true --qdrant-bulk-load false`). It is the only
layout whose RAM could approach the ~43 B/vector budget. int8 and bq2 would pin 77 GB and 19 GB of
quantized vectors alone, more than or most of the container's 24 GB. Bulk mode was ruled out by its
1 GB-per-million-rows anon growth. Float16 originals halve the largest part of the disk.

**The run is incomplete.** One query in the limit 360 / `numCandidates` 5,400 pass ("+ 2 tokens (AND)")
hit Qdrant's server-side search timeout (60 s). The harness did not catch it, and the run stopped
before writing a JSON report. The run script then dropped the collection as planned, so the missing
passes could not be re-run without another 4.7-hour load. Everything below comes from the run log
(`bench-results/bench-stream-qdrant-bq1-cold-f16-acorn-nobulk-100000000x768-20261112-164110-partial.log`).
Not measured: the remaining four filters at 360 / 5,400, exact search, and the 1/4/16-client
throughput test. The harness has since been fixed: the server timeout now follows the client
deadline, and a timed-out query counts as an empty result.

### Recall and latency, limit 24 / numCandidates 360

p50 first / p50 repeat / p99 repeat, in ms. Madelene's figures are from its 100M report (also in the README).

| Filter | Selectivity | Madelene R@24 | Madelene | Qdrant bq1-cold R@24 | Qdrant bq1-cold |
|---|---|---|---|---|---|
| tenant only | 100% | 0.951 | 103 / 66 / 87 | **0.877** | 310 / 709 / 1,312 |
| + busiest source | 21.3% | 0.983 | 124 / 106 / 133 | **0.940** | 800 / 940 / 3,689 |
| + mid source | 0.28% | 0.984 | 65 / 47 / 75 | 1.000 | 288 / 351 / 678 |
| + 2 tokens (AND) | 28.4% | 0.970 | 117 / 98 / 121 | **0.935** | 4,683 / 4,596 / 7,249 |
| + token OR + NOT | 20.8% | 0.972 | 127 / 117 / 148 | **0.943** | 6,338 / 6,422 / 8,368 |
| + last 20% of time | 20.0% | 0.978 | 38 / 38 / 58 | **0.807** | 517 / 526 / 2,335 |
| + mid source + 5% of time | 0.014% | 0.993 | 17 / 13 / 18 | 0.983 | 170 / 179 / 2,230 |

* Qdrant misses recall@24 ≥ 0.95 on five of seven filters; Madelene meets it on all seven.
* Qdrant is 7–55× slower at p50 (repeat): 7× on the mid source, 47–55× on the token filters.
* The repeat pass is no faster than the first. The 261 GB collection cannot stay in the page cache
  (24 GB container), so every pass reads HNSW neighbours, 1-bit codes and float16 originals from NVMe.
  Madelene's 95 GB index does not fit in the 64 GB host either, but its reads are sequential IVF lists.

### limit 360 / numCandidates 5,400 (partial)

| Filter | Madelene R@360 | p50 | Qdrant R@360 | p50 / p99 |
|---|---|---|---|---|
| tenant only | 0.854 | 85 ms | 0.956 | 8.4 s / 12.3 s |
| + busiest source | 0.978 | 120 ms | 0.969 | 14.8 s / 20.2 s |
| + mid source | 0.994 | 83 ms | 1.000 | 0.39 s / 7.1 s |
| + 2 tokens (AND) | 0.967 | 109 ms | — | a query exceeded 60 s; run stopped |

At this candidate count Qdrant's HNSW (`ef` 5,400) is 5–125× slower than Madelene. Recall on
"tenant only" is higher than Madelene 0.1's 0.854, a gap caused by 0.1's per-segment probe budget and fixed
in 0.2 (0.957).

### Throughput

Not measured. From the repeat latencies, one client running the seven filters in turn would average
about 2 s per query, about 0.5 QPS. Madelene 0.1 measured 13.8 / 25.7 / 29.3 QPS at 1 / 4 / 16 clients (0.2: 53.9 / 96.5 / 111.6).

### Operational footprint at 100M

| | Madelene | Qdrant bq1-cold, float16, no bulk |
|---|---|---|
| Ingest | 94,000 rows/s (17.7 min) | 5,911 rows/s average (282 min); 11.2k rows/s for the first 10M, falling to 3.9k for the last 10M |
| Index build after ingest | compaction 63.7 min | 1.6 min (indexing ran during ingest) |
| Fully indexed after | 81 min | 284 min |
| Restart to ready | — (in-process; opens mmaps) | 14 s |
| Disk | 95 GB = **952 B/vector** | 261 GB = **2,612 B/vector** |
| Peak disk during load | — | 286 GB (1.1× final, sampled each minute); the pre-run estimate was 654 GB |
| Non-evictable RAM | managed heap 0.68 GB = **6.8 B/vector** (working set 17.4 GB, the rest mapped pages) | anon 4.5 GB after restart = **45 B/vector**; not sampled after queries |
| Peak non-evictable RAM | — | anon 8.7 GB = 87 B/vector, just after indexing finished; 2.8–5.8 GB during ingest |

Qdrant's disk per vector at 100M is lower than at 10M (2,612 vs 4,205 B) because the originals are
float16 (1,536 B instead of 3,072 B). The 10M runs used float32 originals.

## Extrapolation to 3B vectors

Linear in rows, from the 10M per-vector figures (100M for the last row). The RAM budget is ≤ 128 GB,
about 43 B per vector.

| | Disk at 3B | Non-evictable RAM at 3B | Fits 128 GB? |
|---|---|---|---|
| Madelene | 2.9 TB | ~30 GB heap (10 B/vector; 6.8 B/vector measured at 100M), rest page cache | yes |
| Qdrant int8 | 14.6 TB | 4.3 TB (1,448 B/vector; int8 codes alone are 768 B) | **no** |
| Qdrant bq2 | 12.9 TB | 2.4 TB (814 B/vector) | **no** |
| Qdrant bq1-disk | 12.6 TB | 495 GB (165 B/vector; 1-bit codes alone are 96 B) | **no** |
| Qdrant bq1-cold (nothing pinned), 10M | 12.6 TB | 208 GB (69 B/vector after queries) | **no** |
| Qdrant bq1-cold, float16, measured at 100M | 7.8 TB (2,612 B/vector) | ≥ 135 GB (45 B/vector idle after restart; 87 B/vector = 261 GB at the post-indexing peak) | **no** |

Per configuration, non-evictable RAM per vector against the 43 B/vector budget: int8 1,448 B, bq2
814 B, bq1-disk 165 B, bq1-cold 69 B (10M, after queries) and 45 B (100M, idle). **None can hold 3B
vectors in 128 GB on one node.** int8, bq2 and bq1-disk miss by 4–34×. bq1-cold is the closest: idle it
is just over the budget, but during loading and after queries it runs at 1.6–2× the budget, and that
leaves no page cache at all.

With float16 originals Qdrant's disk drops by 1,536 B/vector, to 7.8 TB at 3B: still 2.7× Madelene.

Pinned quantized vectors alone exceed the budget for every encoding Qdrant offers (1-bit = 96 B/vector =
288 GB at 3B). With nothing pinned (bq1-cold) Qdrant still holds 69 B/vector of non-evictable memory at
10M (process baseline plus per-point bookkeeping; not broken down further), 1.6× the budget, and every HNSW hop becomes a random read
once the collection is larger than the page cache. Fitting 3B in 128 GB would take at least two nodes
before leaving any page cache. See the 100M section for how that layout behaves when the data no longer
fits in the page cache.

## Filter semantics vs MongoDB

`QdrantFilterTranslator` maps the `FilterNode` AST to a Qdrant filter. All filters the MongoDB
parsers accept translate exactly, so the backend never needs to throw for semantic reasons; it throws
`FilterNotSupportedException` only for paths that are not indexed (like the engine) and for an internal
range node whose two bounds have different types.

| MongoDB | Qdrant | Why it is exact |
|---|---|---|
| `{f: v}`, `$in` | `match` / `match any` on a keyword field holding type-tagged terms (`s:src7`, `n:5`, `b:1`, `o:<hex>`, `d:<ms>`, `u:<uuid>`) | Qdrant matches when any array element equals; tagging keeps MongoDB's type brackets (`5` ≠ `"5"`, `5` = `5.0`) |
| `$gt/$gte/$lt/$lte` on the time field | `range` on an integer field (Unix ms) | one value per row; missing time never matches, as in MongoDB |
| ranges on other fields | `range` on `<path>__n` (numbers) or `<path>__d` (dates) | per-type sidecar, so a number bound never matches a date; any element in range matches, as MongoDB's multikey ranges |
| `$and` / `$or` / `$nor` | `must` / `should` / `must_not` | plain boolean logic over the leaves |
| `$ne`, `$nin`, `$not` | `must_not` of the positive leaf | NOT(any element matches) = no element matches; a missing field matches. This is MongoDB's definition. |

Not used: Qdrant's `match except`. On an array it matches when *some* element is outside the list, and it
does not match a missing field, so it is not `$nin`. Qdrant's `is_empty`/`is_null` are not needed because
the parsers reject `$exists` and null equality (MongoDB's `$vectorSearch` pre-filter does not support
them either).

Verification: a property test runs 1,000 random filters (nested AND/OR/NOR/NOT, multi-valued fields,
missing fields, mixed number/string/date values, two-sided ranges on arrays) over 2,000 documents and
compares the translated filter, evaluated with Qdrant's documented rules, against
`DocumentFilterEvaluator`. The opt-in integration test (`MADELENE_QDRANT_URL`) runs 300 random filters
against a real Qdrant 1.20 server and compares the returned id sets with `DocumentFilterEvaluator`: no
differences.

Differences that remain:

* Field paths with `.` are stored flat (escaped). Positional paths and nested-document semantics are out
  of scope, as in the engine.
* Numbers are doubles (same limit as the engine: int64 beyond 2^53 and Decimal128 lose exactness).
* Qdrant point ids must be UUIDs or integers: the backend uses a UUIDv5 of the encoded `_id` and keeps
  the original in the payload (`__id`), returned with each hit.
* Scores: Qdrant returns cosine; the backend maps it to MongoDB's `(1 + cos) / 2`.

## Verdict

For the target (3B 768-d vectors, filtered queries, one machine with ≤ 128 GB of
RAM), Qdrant does not fit and Madelene does.

* **RAM.** Qdrant reaches the recall target only when the quantized vectors and the HNSW graph are in RAM
  (int8: 1,448 B/vector, 4.3 TB at 3B). Every layout that comes near 43 B/vector gives up the recall
  target: 1-bit codes, everything memory-mapped. At 100M that layout already needs about 45 B/vector
  idle, and more while loading.
* **Latency once the data exceeds RAM.** HNSW makes dependent random reads. At 100M, with the collection
  11× larger than the container's memory, Qdrant's p50 was 0.2–6.4 s at limit 24 and 8–15 s at
  `numCandidates` 5,400, against 13–117 ms and 83–120 ms for Madelene 0.1 (0.2: 4.7–21.3 ms at limit 24). Madelene's index was also
  larger than RAM, but its IVF scans read sequentially.
* **Disk and ingest.** Qdrant stores full-precision originals for rescoring: 2.7× Madelene's disk with
  float16, 4.4× with float32. Without bulk mode (which needs RAM proportional to the data) it ingested
  at 4–11k rows/s against Madelene's ~94k.
* **Where Qdrant is better.** At 10M with int8 in RAM it is faster than Madelene on unfiltered and very
  narrow filters (2–3 ms warm), its recall at large `numCandidates` is slightly higher, and it is a
  complete server: replication, sharding, snapshots and an HTTP/gRPC API. With enough RAM across several
  nodes (int8 at 3B is about 4.3 TB of RAM), it would meet the recall and latency targets that a single
  128 GB machine cannot.
* **Filters.** Every `$vectorSearch` filter the parsers accept translates to Qdrant exactly (see above).
  Semantics are not the obstacle; footprint is.

Recommendation: keep Madelene for the single-node, disk-resident case. Qdrant is an option only for a
multi-node deployment sized to hold quantized vectors and graphs in RAM.

## Reports

`bench-results/`:

* Madelene 10M: `bench-stream-10000000x768-20261112-033204.json`
* Qdrant int8: `bench-stream-qdrant-sq8-acorn-restarted-alone-10000000x768-20261112-075559.json`
  (build figures from `bench-stream-qdrant-sq8-acorn-10000000x768-20261112-034947.json`)
* Qdrant bq2: `bench-stream-qdrant-bq2-acorn-os10-alone-10000000x768-20261112-075255.json`
  (build figures and oversampling 4 from `bench-stream-qdrant-bq2-acorn-10000000x768-20261112-041102.json`)
* Qdrant bq1-disk: `bench-stream-qdrant-bq1-disk-acorn-10000000x768-20261112-074936.json`
* Qdrant bq1-disk, no bulk mode: `bench-stream-qdrant-bq1-disk-acorn-nobulk-10000000x768-20261112-081746.json`
* Qdrant bq1-cold, no bulk mode: `bench-stream-qdrant-bq1-cold-acorn-nobulk-10000000x768-20261112-083920.json`
* 1M tuning: `bench-stream-qdrant-sq8*-1000000x768-*.json`
* Madelene 100M: `bench-stream-100000000x768-20261111-123000.json`
* Qdrant 100M (bq1-cold, float16, no bulk mode; log only, run stopped at a query timeout):
  `bench-stream-qdrant-bq1-cold-f16-acorn-nobulk-100000000x768-20261112-164110-partial.log`

Reproduce (Qdrant container on `localhost:6334`, one collection loaded at a time):

```bash
dotnet run -c Release --project tools/Madelene.Bench -- --stream --rows 10000000 --days 10 --queries 50 --ivf-min-rows 500000 --dir D:/bench/10m --keep --backend qdrant --qdrant-config bq1-disk --qdrant-acorn true --qdrant-restart-before-queries true
```
