# Madelene vs Milvus

> Measured on a separate experimental branch of Madelene (the Milvus work). The backend code, deployment files and raw result files it mentions are not part of this package; this report is included for its findings. The Madelene figures in it are version 0.1, and its command lines use options of that branch's benchmark tool that the one in this repo doesn't have. See [the benchmark index](README.md).


Status: measured on synthetic data at 10M rows. 100M was **not run** for Milvus on this machine (see
[100M](#100m-not-run-on-this-machine)). Reports are in `bench-results/` (`bench-stream-madelene-*`,
`bench-stream-milvus-<config>-*`).

## Verdict

For the design goal in [ARCHITECTURE.md](../ARCHITECTURE.md#1-goal-and-budget) (3B × 768-d, filtered by tenant/source/time/tokens, p50 < 1 s, ≤ 128 GB RAM, one
machine), measured on this machine with the configurations below, **Milvus is not a fit for this target.**
Milvus is designed as a distributed service sized across several nodes, which this single-machine budget rules
out.

* **At 10M**, at the same recall (0.97–0.995), Milvus' best configurations are 2–25× slower per query, 8–25× on the broad
  filters (`rabitq` p50 9.5–282 ms, `diskann` 10–131 ms vs Madelene 2.9–17 ms) and reach 16–35 QPS vs 432. Milvus ingests
  2.4× slower, and its indexes keep rebuilding for ~30 min after the data is in.
* **RAM is the deciding problem.** Measured anonymous RAM per vector is 0.26–1.24 KB depending on the index
  (1.0 KB for the only IVF configuration with acceptable recall, even with everything memory-mapped). 3B vectors
  would need 0.8–3.7 TB of RAM against a 128 GB budget (43 B/vector); even 100M doesn't fit the memory this
  machine can give Milvus. Madelene keeps no per-vector data in RAM and ran 100M on a 64 GB host.
* **Disk** is ~7× larger at steady state (65 GB vs 9.5 GB at 10M; 160+ GB during ingest and builds): Milvus keeps
  float32 vectors in its binlogs (3.2 KB/vector) plus a WAL and index files (≈ 13 TB at 3B vs Madelene's 2.9 TB),
  and needs tuning to garbage-collect.
* **Operations:** three stateful services (and a cluster of them at 3B) instead of a library, MongoDB filter
  semantics only through a translation layer that has to refuse some filters, and a second copy of the data.

Milvus would make sense if the vectors were in a cluster with terabytes of RAM anyway, or for exact-recall
workloads where DISKANN's 1.0 recall matters more than latency and throughput.

## What was compared

* **Same rows, queries, filters and ground truth.** `--stream --rows 10000000 --days 10`, seed 7, 100 text-like
  queries, the 7 filters of `Scenario.All`, query configs limit 24 / numCandidates 360 and limit 360 / 5,400, then
  1/4/16 clients for 20 s each mixing all filters. Recall is against the same cached exact top-k.
* **Same contract.** Milvus is driven through `src/Madelene.Milvus` (`IVectorSearchBackend`), the same
  `$vectorSearch` request and `FilterNode` AST the MongoDB seam uses; latency is the client's wall time per search
  (gRPC round trip included). Scores are normalized to `(1 + cos) / 2`.
* **Milvus 3.0.6 standalone** (Milvus + etcd + MinIO, `deploy/milvus/docker-compose.yml`), official images,
  official .NET SDK `Milvus.Client.V2` 3.0.0 over gRPC. Collection: VARCHAR primary key, 768-d FLOAT_VECTOR,
  `tenantId` VARCHAR partition key, `sourceId` VARCHAR, `tags` ARRAY&lt;VARCHAR&gt;, `timestamp`
  INT64 (Unix ms), INVERTED indexes on the three filter fields, STL_SORT on the time field.
* **Machine:** 24 cores, 64 GB RAM, NVMe, Windows 11. Docker Desktop (WSL2) has 31 GB, of which ~15 GB
  was already in use, so the Milvus container was limited to **14 GB**. Madelene runs in
  the host process with no limit (its 10M working set was 5.4 GB). Keep that asymmetry in mind for latency;
  the RAM numbers below are measured, not limits.

Milvus configurations (chosen for RAM-limited, very large collections):

| Config | Index | Build params | Search params |
|---|---|---|---|
| `rabitq` | IVF_RABITQ, 1-bit codes + SQ8 refine copy | nlist 1024, refine SQ8 | nprobe 32 (raised for selective filters, see below), refine_k = numCandidates / limit |
| `diskann` | DISKANN (Vamana graph + full vectors on local disk, PQ in RAM) | max_degree 56, search_list_size 100 | search_list = numCandidates |
| `aisaq` | AISAQ (DiskANN variant, PQ codes on disk too) | max_degree 56, search_list_size 100, inline_pq -1 | search_list = numCandidates |

HNSW_SQ was not run: its index is RAM-resident (~9 GB at 10M plus the scalar data), so it doesn't fit the
14 GB container next to everything else, and at 3B it needs terabytes of RAM.

**Adaptive nprobe (IVF only).** With a fixed nprobe, IVF probes the same few lists whatever the filter; for a
filter matching 0.3% of rows that leaves too few matching rows in them and Milvus returns short results (1M smoke
run: recall@24 0.36–0.56 on the two selective filters). The backend therefore counts the filter's matches first
(`count(*)`, ~3 ms from the scalar indexes, included in the latency) and raises nprobe so that ~8 × numCandidates
matching rows are probed, up to all lists — the same rule as Madelene's `ProbeCandidateFactor`
(`MilvusBackendOptions.AdaptiveProbeFactor`). That fixed it (0.99 on both).

## Results at 10M

Repeat pass unless noted; "first" is the first time each query runs.

| | Madelene | Milvus `rabitq` | Milvus `diskann` | Milvus `aisaq` |
|---|---|---|---|---|
| Recall@24 (7 filters) | 0.979 – 0.995 | 0.966 – 0.995 | **0.999 – 1.000** | 0.492 – 1.000 |
| p50, limit 24 | **2.9 – 17 ms** | 9.5 – 282 ms | 10.5 – 131 ms | 6.2 – 52 ms |
| p50 first pass | **3.4 – 18 ms** | 9.4 – 289 ms | 6.4 – 107 ms | 6.9 – 51 ms |
| worst p99, limit 24 | **40 ms** | 308 ms | 234 ms | 74 ms |
| Recall@360 (5,400 cand.) | 0.988 – 0.995 | 0.981 – 0.994 | 1.000 | 0.992 – 1.000 |
| p50, limit 360 | **3.1 – 18 ms** | 11 – 284 ms | 12 – 1,058 ms | 23 – 364 ms |
| QPS at 1 / 4 / 16 clients | **107 / 290 / 432** | 10.3 / 15.0 / 15.9 | 27.0 / 34.6 / 35.1 | 21.2 / 27.7 / 29.9 |
| p50 at 16 clients | **37 ms** | 966 ms | 454 ms | 528 ms |
| Ingest | **79k rows/s** (2.1 min) | 33k rows/s (5.0 min) | (same data) | (same data) |
| Build after ingest | compaction + IVF 6.8 min | compaction 4.9 min + index 31.8 min | index 63 min | index 64.5 min |
| Searchable / settled after first write | buffer is searched at once; all IVF 8.9 min | 41.9 min (see below) | — | — |
| RAM after load | process working set 5.4 GB (0.1 GB heap, rest mapped files) | 14.2 GB (12.3 GB anonymous) | 13.6 GB (11.6 GB anon.) | 4.0 GB (3.4 GB anon.), **9.4 GB after queries** |
| Disk | **9.5 GB (951 B/vector)** | 162 GB of volumes with default GC (see below) | 165 GB | 160 GB |

Per filter, limit 24 / 360 candidates, repeat pass (recall@24 / p50 ms):

| Filter | Selectivity | Madelene | `rabitq` | `diskann` | `aisaq` |
|---|---|---|---|---|---|
| tenant only | 100% | 0.979 / 16.9 | 0.966 / 282 | 1.000 / 96.5 | 0.881 / 51.7 |
| + busiest source | 21.3% | 0.982 / 8.5 | 0.970 / 66.9 | 1.000 / 69.3 | 0.492 / 47.6 |
| + mid source | 0.28% | 0.995 / 7.0 | 0.995 / 11.0 | 1.000 / 28.4 | 1.000 / 26.0 |
| + 2 tokens (AND) | 28.4% | 0.980 / 9.9 | 0.971 / 87.5 | 1.000 / 128 | 0.530 / 47.3 |
| + token OR + NOT | 20.8% | 0.980 / 9.9 | 0.970 / 67.7 | 0.999 / 131 | 0.495 / 48.2 |
| + last 20% of time | 20.0% | 0.982 / 4.2 | 0.970 / 107 | 1.000 / 41.2 | 0.793 / 34.3 |
| + mid source + 5% of time | 0.014% | 0.990 / 2.9 | 0.988 / 9.5 | 1.000 / 10.5 | 1.000 / 6.2 |

What the numbers say:

* **`rabitq`** reaches the same recall as Madelene, but its cost grows with the number of matching rows
  (tenant only 282 ms vs 17 ms) and it saturates at ~16 QPS: each query costs about a CPU-second across the
  36–38 segments. Memory diagnostics (cgroup `memory.stat`) show only 639 major faults during the whole query
  phase, so this is CPU, not the 14 GB limit. A second run on the same data reproduced it (12.4 / 17.9 / 16.7 QPS).
* **`diskann`** has the best recall (it reranks with full float vectors from disk) and ~2× `rabitq`'s
  throughput, but `numCandidates` → `search_list` makes limit 360 / 5,400 candidates cost up to a second, and it
  did 474k major page faults in the query phase (its disk reads go through the container's small page cache).
* **`aisaq`** keeps the least in RAM after load, but with Milvus' defaults its recall on the 20–30% filters is
  0.49–0.53 at search_list 360; with search_list 5,400 (limit 360) it is 0.99+. It needs a larger search_list per
  filter selectivity than DiskANN, at a latency cost. Its RAM grows during queries (3.4 → 8.9 GB anonymous) as its
  read cache fills.
* **Madelene** is 2–25× faster per query (8–25× on the broad filters), has 12–27× higher throughput at equal or better recall than every
  Milvus configuration here, ingests 2.4× faster and is searchable sooner.

Two more `rabitq` variants on the same data, to find a configuration that could scale (reports
`bench-stream-milvus-rabitq-mmap-*`, `bench-stream-milvus-rabitq-norefine-*`):

| | `rabitq` | `rabitq` + mmap everything | `rabitq` without refine, mmap everything |
|---|---|---|---|
| Recall@24 | 0.966 – 0.995 | 0.966 – 0.995 | **0.201 – 0.902** |
| p50, limit 24 | 9.5 – 282 ms | 9.3 – 258 ms | 9.9 – 268 ms |
| QPS 1 / 4 / 16 | 10.3 / 15.0 / 15.9 | 10.3 / 15.4 / 14.6 | 10.5 / 15.6 / 14.9 |
| Anonymous RAM after queries | 12.4 GB | 10.1 GB | 2.6 GB |

"mmap everything" sets `mmap.enabled` on the collection (raw field data) and on every index (STL_SORT can't be
mapped). It moved the scalar data and indexes (~2.3 GB) out of anonymous memory, but the IVF_RABITQ index with
its SQ8 refine copy stays resident (~1 KB/vector). Dropping the refine copy saves that RAM but leaves 1-bit codes
alone to rank thin-margin CLIP-like neighbours: recall@24 0.20–0.30 on the broad filters. Throughput did not
change in either variant, so the per-query cost is not the refine step.

### Settling: Milvus keeps re-indexing after the data is in

After the flush and an explicit compaction, every row was indexed within ~16 min, but Milvus' background mix
compaction kept merging segments and the merged segments needed new indexes: "rows pending index" went *up*
again from 0 to 7.5M before it reached 0, **31.8 min after the compaction** (41.9 min after the first insert).
Queries during that time run on a mix of indexed and brute-force-scanned segments. For a workload that ingests all
day this is a permanent background cost (one full index build per merge level); Madelene's equivalent is its own
compaction (6.8 min at 10M), after which nothing is rebuilt.

## Operational footprint

| | Madelene | Milvus standalone |
|---|---|---|
| Processes / services | a library in the host process | 3 containers: milvus (all coordinators and nodes), etcd (metadata), MinIO (object storage); production clusters split milvus into proxy, coordinators, query/data/streaming nodes |
| Configuration | index definition JSON | compose file + `milvus.yaml` overrides (`deploy/milvus/user.yaml`) |
| Durability | segments + manifest; host change stream is the log | WAL (RocksMQ in standalone) + binlogs in MinIO + metadata in etcd |
| Memory at 10M | 5.4 GB working set (mostly mapped files, reclaimable) | 12–14 GB in the container, mostly anonymous (not reclaimable), plus 1.5 GB MinIO |
| Disk at 10M | 9.5 GB | 160–165 GB of volumes during the runs; 65.5 GB at the end (MinIO 32.9 GB, local 32.5 GB, etcd 0.08 GB) once garbage collection caught up |

**Where Milvus' disk goes (10M rows, after the `rabitq` run, default settings):**

| Location | Size | What |
|---|---|---|
| MinIO `insert_log` | 93 GB | binlogs: 32 GB live (3.2 KB/row: float32 vector + scalars), the rest are copies of segments replaced by sorting and compaction, kept until GC (default: dropped segments are deleted after 3 h, GC runs hourly) |
| MinIO `index_files` | 21 GB | ~10 GB live IVF_RABITQ+SQ8 index, plus indexes of replaced segments |
| `rdb_data` | 31 GB | RocksMQ WAL: retained 3 days / 8 GB per topic, and RocksDB only reclaims space in a daily compaction |
| `data/cache` | 8.5 GB | local copy of the loaded index files |
| peak drive growth | 195 GB | during ingest + compaction + index build |

With `deploy/milvus/user.yaml` (GC every 5 min, 10 min tolerance; WAL retention 5 min, RocksDB compaction every
10 min), `insert_log` dropped to the 32 GB of live data within the hour. Disk indexes add their own local copies:
`diskann`/`aisaq` keep ~45 GB of index files in the local volume on top of the MinIO copy.

## Extrapolation to 3B vectors

Target: 3B × 768-d, ≤ 128 GB RAM, i.e. **≈ 43 bytes of RAM per vector** for everything.

**RAM**, measured at 10M (anonymous memory of the Milvus container after the query phase, which the kernel can't
page out the way it drops mapped file pages), scaled linearly:

| Configuration | RAM / vector (measured) | 3B needs | Fits 3B in 128 GB? |
|---|---|---|---|
| Madelene | no per-vector RAM: 0.1 GB heap at 10M, 0.7 GB at 100M; the rest of the working set (5.4 GB at 10M, 17 GB at 100M on a 64 GB host) is page cache over mapped segment files | page cache for the probed lists; design budget 43 B/vector | **yes, by design**; 100M ran with 64 GB and a 95 GB index |
| Milvus `rabitq` (IVF_RABITQ + SQ8 refine) | 1.24 KB (1.0 KB with mmap everything) | 3.0 – 3.7 TB | **no** (24–29×) |
| Milvus `diskann` | 1.18 KB (PQ codes at 12.5% of raw + 10% node cache + scalars) | 3.5 TB | **no**; lowering `pq_code_budget_gb_ratio`/cache trades recall, and the scalar/PK part alone is too big (next rows) |
| Milvus `aisaq` | 0.34 KB after load, 0.89 KB after queries (read cache) | ≥ 1 TB | **no** |
| Milvus `rabitq` without refine | 0.26 KB; subtracting the ~0.11 KB of resident 1-bit codes leaves roughly 0.15 KB that is not vector index (primary key, segment statistics, process baseline) | 0.8 TB | **no**, and recall is 0.2–0.3 |

The floor is the roughly 150 B/vector (an estimate, see the last row) that Milvus keeps for the primary key,
segment statistics and other per-row state even with everything mappable mapped: 3B × 150 B ≈ 450 GB, 3.5× the whole budget before any vector data. ≤ 128 GB per
machine at 3B is therefore not realistic for Milvus standalone; it means a distributed cluster sized for
~1–3.7 TB of query-node RAM (roughly 10–30 nodes of 128 GB), plus the coordinators, etcd and object storage.

**Disk**, measured at 10M after garbage collection caught up:

| | Madelene | Milvus |
|---|---|---|
| Vector + filter data | 951 B/vector in total (1-bit codes, int8 vectors, postings, ids) | 3.2 KB/vector binlogs (float32 vector + scalars) in object storage |
| Index | included above | IVF_RABITQ + SQ8 ≈ 1 KB/vector; DiskANN/AISAQ several KB/vector, kept twice (object storage + local disk) |
| Write-ahead log | none (MongoDB's change stream is the log) | RocksMQ held ~the full data size (31 GB at 10M) with default retention, and was not trimmed after lowering retention |
| Transient | compaction rewrites a bucket at a time | segments replaced by sorting/compaction stay until GC (3 h by default): 93 GB of binlogs for 32 GB live at 10M, 195 GB peak growth |
| **3B** | **≈ 2.9 TB** | **≈ 13 TB steady** (9.6 TB binlogs + 3 TB IVF_RABITQ index), more with the WAL and transients; float16 vectors would halve the binlogs |

**Time** at 3B on one machine, from the 10M rates: Milvus ingest 33k rows/s → 25 h (Madelene 79–94k/s →
9–10 h); IVF_RABITQ index builds ~15 min per 10M → ~75 h; DISKANN/AISAQ ~64 min per 10M → ~13 days, before
background re-indexing after compactions.

## 100M: not run on this machine

A Madelene 0.1 100M baseline exists (`bench-stream-100000000x768-20261111-123000.json`: p50 13–117 ms,
recall@24 0.95–0.99, 17 GB working set on the 64 GB host, 95 GB on disk). A matching Milvus run was not done:
every Milvus configuration measured at 10M needs more RAM at 100M than this machine can give it, and the one that
might fit (AISAQ) would hold the benchmark machine for ~13 h with untuned recall.

**Measured at 10M** (per vector = value / 10M):

| Config | Anon. RAM, no mmap | Anon. RAM, mmap everything | Binlogs | WAL | Index files | Build after ingest |
|---|---|---|---|---|---|---|
| `rabitq` (IVF_RABITQ + SQ8) | 12.4 GB (1.24 KB) | 10.1 GB (1.01 KB) | 30–32 GB (3.0–3.2 KB) | 31 GB (3.1 KB) | ≈ 10 GB (≈ 1 KB)¹ | compaction 4.9 min + 31.8 min to settle; 15.3 min as a rebuild |
| `rabitq` without refine | — | 2.6 GB (0.26 KB) | same | same | 1.6 GB (0.16 KB) | 15.0 min |
| `diskann` | 11.6 GB load, 11.8 GB after queries (1.18 KB) | not measured | same | same | ≈ 28 GB object storage + ≈ 45 GB local¹ | 63 min |
| `aisaq` | 3.4 GB load, 8.9 GB after queries (0.34–0.89 KB) | not measured | same | same | similar to `diskann`¹ | 64.5 min |
| Madelene | 0.1 GB heap; 5.4 GB working set is mapped files | — | 9.5 GB total on disk (951 B) | none | included | 6.8 min compaction |

¹ Derived from directory sizes taken while two index generations or other files shared the volume (MinIO
`index_files` 21 GB with two IVF_RABITQ generations; 48 GB with DISKANN plus the replaced IVF_RABITQ; local
volume 80 GB including the 31 GB WAL), so ± a few GB.

Ingest was 33k rows/s for Milvus (5.0 min for 10M) and 79k rows/s for Madelene (2.1 min).

**Extrapolated to 100M** (×10, linear; not measured), against what this machine offers: 64 GB host RAM, 31 GB
for Docker of which ~14 GB is free for Milvus, Docker's virtual disk capped at 1 TB (~800 GB free):

| Config | RAM needed | Fits 14 GB? | Disk needed (binlogs + WAL + index, steady) | Time: ingest + compaction + index |
|---|---|---|---|---|
| `rabitq` | 101–124 GB | no | 320 + 310 + 100 ≈ 730 GB, plus transient copies | 50 min + ~50 min + 2.5–5 h |
| `rabitq` without refine | ~26 GB | no (and recall@24 0.20–0.30) | ≈ 650 GB | ~4 h |
| `diskann` | ~118 GB | no | ≈ 1.4 TB (two index copies) | 50 min + ~50 min + ~10.5 h |
| `aisaq` | 34 GB after load, up to ~89 GB as its read cache fills (the cache may be bounded by config; not tested) | uncertain at best | ≈ 1.3 TB | 50 min + ~50 min + ~10.8 h ≈ 13 h |
| Madelene (measured at 100M) | 0.7 GB heap, 17 GB working set (page cache) | runs on the host | 95 GB | 17.7 min + 64 min |

Against the 3B / 128 GB budget (43 B/vector), every Milvus configuration is 6–29× over on RAM alone (see the
extrapolation above).

**Recall trade-offs found at 10M:**

* IVF_RABITQ **without** the SQ8 refine copy: recall@24 0.20–0.30 on the broad filters (0.90 only on the most
  selective one), 0.40–0.70 at limit 360. Not usable for these embeddings; with refine it is 0.966–0.995.
* AISAQ with Milvus' default build and `search_list` = numCandidates (360): recall@24 0.49–0.53 on the 20–28%
  filters, 0.79–0.88 on the 20%-time and tenant-only filters, 1.0 on the selective ones. With `search_list` = 5,400
  (limit 360) recall@360 was 0.99–1.00 at 150–364 ms p50. A sweep of larger `search_list` at limit 24 (recall vs
  p50) is **untested**: it needs the AISAQ index rebuilt (64 min) while the machine was needed for the other
  engine. `--milvus-reuse true --milvus-index aisaq --milvus-search "search_list=1000" --milvus-candidates none`
  runs it without a rebuild once an AISAQ index exists.
* DISKANN with `search_list` = numCandidates: recall 0.999–1.000, but 0.7–1.1 s p50 at limit 360 / 5,400.

**What a 100M Milvus run would need** (to run it later elsewhere): for `rabitq`, a Milvus process with
≥ 128 GB of RAM it can keep (≈ 101–124 GB measured-and-scaled anonymous memory plus headroom) and ≈ 1 TB of
disk; for `aisaq`, ≥ 48–96 GB depending on its read cache, ≈ 1.3–1.5 TB of disk and ~13 h of ingest, compaction
and graph builds on 24 cores (Vamana builds ran on ~12 threads), plus a `search_list` sweep at 10M first. In all
cases: `deploy/milvus/user.yaml` (fast GC) or at least 2× the binlog size free for compaction transients, and a
WAL that is actually trimmed (RocksMQ kept ~the full data size here).

```bash
dotnet run -c Release --project tools/Madelene.Bench -- --stream --backend milvus --rows 100000000 --days 100   --bucket-days 1 --queries 50 --ivf-min-rows 500000 --dir <dir with truth-100000000-7-50-360-100-200.json> --keep   --milvus-index aisaq --milvus-mmap-all true --milvus-require-free-gb 1500 --milvus-build-hours 14
```

## Filter semantics: Milvus vs MongoDB

`src/Madelene.Milvus/MilvusFilterTranslator.cs` maps the `$vectorSearch` filter AST to a Milvus boolean
expression (with expression templates, so values are never spliced into the text). A property test
(`MilvusFilterTests.TranslatedFiltersAgreeWithMongoSemanticsOrThrow`) evaluates the generated expressions on the
exact column values the backend stores, with Milvus' three-valued null logic, and checks them against
`DocumentFilterEvaluator` on 2,000 random filters × 1,500 random documents. The opt-in integration test
(`MADELENE_MILVUS_URI=localhost:19530`) checks exact results and scores against brute force on a real server,
upserts that change the partition key, deletes, and the MongoDB seam.

| MongoDB behaviour | Milvus behaviour | What the backend does |
|---|---|---|
| A field can hold values of any BSON type; `1`, `1.0`, `NumberLong(1)` are equal, `"1"` is not. | Every field has one declared type. | Default storage is `ARRAY<VARCHAR>` of type-tagged terms (`n:1`, `s:1`, `b:1`, `d:<ms>`, …), so equality follows MongoDB type brackets exactly. Typed fields (`Keyword`, `KeywordArray`) accept strings only and reject other values at write time; a query value of another type folds to "never matches". |
| Arrays are multikey: `{tags: "a"}` matches if any element is `"a"`. | Arrays need `array_contains` / `array_contains_any`. | `$eq`/`$in` on array fields become `array_contains(_any)`. |
| `$ne`, `$nin`, `$not`, `$nor` match documents where the field is **missing**. | `NULL` is unknown: `not (f == "x")` drops rows where `f` is null. | Array fields are stored as empty arrays, never null, so their negations are exact (the property test covers it). A nullable scalar field (`Keyword` with `Nullable = true`) throws `FilterNotSupportedException` for any negation that touches it. |
| Missing time field: a range is false, its negation true. | — | Time is `INT64` Unix ms with `long.MinValue` as "missing"; an upper-bound-only range adds `ts > MIN`. |
| Range operators on any ordered type. | Ranges on numeric scalars only. | Ranges are supported on the time field; ranges elsewhere throw `FilterNotSupportedException`. |
| `exact: true` (ENN). | No exact search on an indexed field. | Throws `FilterNotSupportedException` (host falls back to MongoDB). |
| A write is visible to the next read. | Bounded staleness by default (a few seconds); Strong waits for the WAL. | `MilvusBackendOptions.Consistency` (bench: Bounded). |
| Scalar values of a document are whatever the document holds. | A partition-key or typed field must have exactly the declared shape. | Writes that don't fit the layout (two sources, a number in a string array, an id over 512 bytes) throw `ArgumentException` instead of being stored inexactly. |
