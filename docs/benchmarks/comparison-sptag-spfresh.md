# Madelene vs SPTAG (SPANN) and SPFresh

> Measured on a separate experimental branch of Madelene (the SPTAG/SPFresh work). The backend code, deployment files and raw result files it mentions are not part of this package; this report is included for its findings. The Madelene figures in it are version 0.1, and its command lines use options of that branch's benchmark tool that the one in this repo doesn't have. See [the benchmark index](README.md).


**Short version.** SPFresh runs here without SPDK: SPTAG's main branch contains SPFresh's update code with a
plain-file backend. Under the same 24 GB limit, on the same 10M rows and filters:

* **Fresh results.** SPFresh kept results fresh through four days of churn, at flat recall@24 ~0.95. Static
  SPTAG/SPANN can't update at all: it was stale (recall 0.46–0.87) until a nightly rebuild of nearly every day bucket.
* **Madelene was ahead on:**
  * recall: 0.977–0.999;
  * writes that are searchable at once: 16–40× faster than SPFresh;
  * broad-filter p50: 5–18 ms, vs 70–79 ms for SPFresh after four days;
  * disk: a third or less (951 B/vector).
* **RAM is more mixed.** Madelene's managed heap is ~10 B/vector, and it was 58 B/vector of anon memory after the
  static queries. But after the daily compactions .NET's GC kept ~11 GB committed: more than SPTAG's 3.8–4.7 GB at
  10M. Version 0.2 returns that memory after each compaction. Per vector at 3B, either SPTAG layout needs ≥ 56 B/vector of pinned RAM: over the
  43 B budget.
* **SPANN can't filter.** All the filtering had to be written around it.
* **SPTAG problems found along the way.** It needed workarounds for a posting corruption, a build step that never
  finishes, and search state shared between indexes in one process.

Details and caveats below.

## What was tested, and from where

| | Source | License | Used here |
|---|---|---|---|
| SPTAG | github.com/microsoft/SPTAG, main @ `cafdd0abf261a8b26fbbb5ece132a46c823572f5` (2026-09-29) | MIT | yes: SPANN with `Storage=STATIC` ("SPTAG/SPANN") and `Storage=FILEIO` ("SPFresh") |
| SPFresh (paper artifact) | github.com/SPFresh/SPFresh @ `5893eb61ee3b18610b6b00f1939be7dae1af8904` (2025-05-31) | MIT | read only (see below) |
| zstd (SPTAG submodule) | github.com/facebook/zstd @ `e47e674cd09583ff0503f0f6defd6d23d8b718d3` | BSD | built with SPTAG |

Everything was built from those repositories at those commits inside `deploy/sptag/Dockerfile` (Ubuntu 22.04,
gcc 11, Boost 1.74, TBB). Nothing in SPTAG was patched.

## Feasibility (verified in the source)

**SPFresh lives in SPTAG's main branch now.** The LIRE protocol from the SPFresh paper (append to a posting,
split an oversized posting into two, merge small ones, reassign the vectors near a split) is
`AnnService/inc/Core/SPANN/ExtraDynamicSearcher.h` (`Append`, `Split`, `MergePostings`, `Reassign`,
`CollectReAssign`). SPANN picks its posting store with the `Storage` parameter (`DefinitionList.h`):

| `Storage` | What it is | Needs |
|---|---|---|
| `STATIC` (default) | Original SPANN: posting lists written once by the build | a file |
| `FILEIO` | SPFresh's dynamic index on one regular file per index: `O_DIRECT` reads/writes through Linux AIO (raw `io_submit` syscalls, no libaio), a block pool grown with `fallocate` | a file system that supports `O_DIRECT` |
| `SPDKIO` | SPFresh on a raw NVMe device through SPDK | SPDK + DPDK + isa-l_crypto built (`-DSPDK=ON`), NVMe bound to `uio`/`vfio`, hugepages, Secure Boot off (kernel lockdown blocks user-space PCI access) |
| `ROCKSDBIO` | SPFresh on RocksDB | a RocksDB fork (github.com/PtilopsisL/rocksdb), `-DROCKSDB=ON` |

* **Does SPFresh need SPDK, raw NVMe or hugepages?** Only the `SPDKIO` backend does. The paper's numbers used SPDK on
  Azure L16s_v3 VMs. `FILEIO` needs none of it. `SPDK`, `ROCKSDB` and `URING` are `OFF` by default in SPTAG's CMake.
* **Does a file-backed mode run in a Linux container on Docker Desktop/WSL2?** Yes. `FILEIO` ran on a Docker volume
  (ext4 inside Docker's virtual disk) with no WSL or Docker setting changed and no raw disk. The standalone SPFresh
  repository (the paper artifact) has no file backend: only RocksDB (the fork above) and SPDK, and its CMake links
  SPDK unconditionally. So "SPFresh" in this document means **SPTAG main's `FILEIO` backend**: the same LIRE code,
  not the paper's SPDK setup. Expect the paper's SPDK numbers to be better than `FILEIO` on a virtual disk.
* **Does static SPANN have an update path?** No. `SPANN::Index::AddIndex` returns `Fail` with "Only Support KV Extra
  Update" when `Storage=STATIC`, and `ExtraStaticSearcher` has no `DeleteIndex`. Updates to a static SPANN index mean
  rebuilding it.
* **Does SPTAG build on Windows?** The repository has Visual Studio solutions and an MSVC branch in CMake ("only MSVC
  version 14"), and `FILEIO` has an IOCP code path. Not attempted: every engine here runs in a Linux container so all of
  them sit under the same cgroup memory limit.
* **Bindings.** SWIG wrappers (`Wrappers/inc/CoreInterface.h`, class `AnnIndex`) for Python, Java and C#: build
  (`Build`, `BuildSPANN`, with metadata), `Add`/`AddWithMetaData`, `Delete`/`DeleteByMetaData`, `Search`,
  `BatchSearch`, `GetIterator`, `Save`/`Load`, `SetBuildParam`/`SetSearchParam`. The C# wrapper is .NET Framework only
  (CMake looks for `csc.exe` under `%windir%\Microsoft.NET\Framework64`), so it can't be used from .NET 10 on Linux.
  This comparison uses a small C++ server instead (below).
* **Filters.** SPANN has none: `SPANN::Index::SearchIndexWithFilter` logs "Not Support Filter on SPANN Index" and
  returns `Fail`. (The in-memory BKT/KDT indexes have a metadata callback; SPANN's head index is BKT but the callback
  isn't wired through.) The only hook is the iterative search, `GetIterator(query, …, filterFunc)`, which walks posting
  lists outward from the query in head-distance order and can skip vectors whose metadata fails a callback. Metadata
  lives in a `MetadataSet` whose offsets are in RAM (8 B/vector).

## Setup

### Server and backend

`deploy/sptag/server/server.cpp` (`madelene-sptag-server`) links `SPTAGLibStatic` and serves a small binary TCP
protocol. `src/Madelene.SPTAG` (`SptagVectorBackend : IVectorSearchBackend`) is the client: the same `$vectorSearch`
contract as the embedded engine (ids, MongoDB-normalised scores, the same filter AST, `FilterNotSupportedException`
for what it can't express).

* **One SPANN index per time bucket** (1 day here, like Madelene's segments). A query only searches buckets that
  overlap the time range implied by its filter.
* **Side tables per bucket**, owned by the server, all file-backed `mmap` (page cache, evictable): int8 vectors by
  slot (input to builds, rebuilds and brute force), a 128-byte filter record per row (time + up to 29 term ids), and
  the external id. Slot = SPTAG vector id inside a bucket.
* **Non-evictable state**: SPANN's head indexes (SPTAG), an id → (bucket, slot) hash map (16 B per slot, 0.7 load
  factor) for updates and deletes by id, a tombstone bit per slot, and the term dictionary.
* **Vectors** are int8: `round(x · s)` of the unit vector with one global scale `s = 127 / max|x|` estimated from a
  20k-row sample (≈ 648 for this data), and L2 distance, which ranks unit vectors like cosine. The score is
  `(1 + cos) / 2` with `cos = 1 − d / 2s²`. (SPTAG's int8 cosine assumes norm 127, which wastes most of the int8 range
  on these vectors.) Madelene also rescores with int8 vectors.
* **Static mode** (`Storage=STATIC`): deletes are server-side tombstones (applied while filtering); inserted and
  updated rows are written to the side tables but stay invisible until `Build`, which builds new buckets and rebuilds a
  bucket from its live rows once tombstoned + pending rows exceed 5 % of it.
* **Dynamic mode** (`Storage=FILEIO`, SPFresh): inserts go through `AddIndexSPFresh` from 8 threads (as SPTAG's own
  SPFresh driver does; it returns the vector ids it assigned, which the server maps to slots in a file-backed table),
  deletes through `DeleteIndex` (SPFresh's version map). The initial 10M rows are bulk-built. `AddIndex` can't add to
  an empty index, so a bucket that doesn't exist yet is bootstrapped by a build once it has 50,000 rows. `Build` waits
  until SPFresh's background split/merge/reassign queue has drained, then calls `Checkpoint()` and `malloc_trim`. The
  server also calls `malloc_trim` after every SPANN build, so RSS reflects what the indexes keep.

* **One process, many SPANN indexes: a pitfall.** SPTAG keeps SPANN search workspaces in
  `ThreadLocalWorkSpaceFactory`, whose workspace is a `static thread_local`: one per thread per workspace type, shared by
  every SPANN index in the process. A thread that searched bucket A hands A's workspace, including its async-I/O request
  bindings, to bucket B. In an early run, recall of identical queries dropped after the 16-client load test
  (for example 0.958 → 0.772 on "last 20 % of time") with the same number of vectors scanned
  (`bench-results/sptag-static-shared-workspace-symptom.log`). The server now gives every bucket's index its own search
  threads (`bucket_search_threads`, 12), so workspaces never cross indexes. With that change the same post-load pass
  matches the pre-load pass exactly. All SPANN numbers below come from that version. A single-index process (the
  SPFresh layout) isn't affected.
* **`SearchInternalResultNum` is not safely mutable on a static index.** SPTAG marks it "Mutable". Raising it at
  runtime from 128 to 256 on the static per-day index returned garbage (recall 0.000–0.315 on five filters) and then
  the server died. A likely cause is in `ExtraStaticSearcher::InitWorkSpace`: when an existing workspace grows, the
  added I/O requests don't get the workspace id that only first-time initialisation assigns. The final static run sets
  it once (64) and never raises it. The dynamic (SPFresh) searcher went from 64 to 512 without trouble.

### Filter strategy

SPANN can't filter, so the server does it around SPANN, in three layers:

1. **Time pruning.** Buckets (or, in a single index, 64k-slot chunks) whose time range doesn't overlap the query's
   are skipped.
2. **Post-filtering with progressive widening.** In each remaining bucket the server opens SPANN's iterative search
   (`GetIterator`) and pulls vectors nearest-postings-first, keeping those whose filter record matches. It reads at
   least as deep as a plain SPANN search: until the iterator reports `RelaxedMono`, which SPTAG sets once the first
   `SearchInternalResultNum` posting lists are drained. It stops there if it has gathered
   `max(limit, numCandidates / buckets searched)` matches. Otherwise it keeps widening, 32 posting lists at a time, up
   to `max-scan` (200,000) vectors. The nearest `limit` matches win. SPTAG's own iterator filter callback works the same
   way but needs a metadata set in RAM; checking the server's records costs no RAM. `SearchInternalResultNum` (how
   many posting lists that first pass reads) was calibrated per layout right after the build, aiming at recall@24 ≥ 0.95
   at `numCandidates` 360 on every filter matching ≥ 1 % of rows. Both layouts hit a plateau just below or at 0.95. Per-day
   SPANN: 0.948–0.958 at 64 and the same at 128, so **64** (SPTAG's default). Single-index SPFresh: 0.805–0.830 at 64,
   0.922–0.934 at 128, 0.945–0.957 at 256 and the same at 512 for twice the latency, so **256**. The plateau is the
   int8 quantization: exact search over the same int8 vectors reaches 0.963–0.992.
3. **Brute-force fallback (hybrid).** Before searching a bucket, the server evaluates the filter on 4,096 evenly
   spaced rows of it. If fewer than 20,000 rows are estimated to match, it skips SPANN and scans that bucket's
   matching rows' int8 vectors exactly. Post-filtering collapses exactly there: at 1M rows, post-filtering alone gave
   recall@24 0.71 for the 0.28 % source filter and 0.44 for the 0.013 % filter.

Every table reports the hybrid. The 10M static run also repeats each query with post-filtering only (no fallback), so
SPANN's own filtered behaviour is visible.

Neither SPTAG nor SPFresh provides layers 1 or 3, the filter records, or the id map: all of that is server code
written for this comparison (about 1,000 lines of C++). Layer 3 is the same idea as Madelene's exact scan of narrow
filters, but without posting lists: it reads every filter record in the time range.

### Configurations

| Name | Layout | SPANN settings | Updates |
|---|---|---|---|
| **Madelene** | 1-day segments, IVF ≥ 500k rows (`--ivf-min-rows 500000`) | — | defaults: tombstone + write buffer, `Flush` + `Compact` as the daily maintenance |
| **SPTAG/SPANN** (`Storage=STATIC`) | one SPANN index per day | heads 3 % (`SelectHead.Ratio=0.03` with `SelectThreshold`/`SplitThreshold`/`SplitFactor` = 0), `ReplicaCount=4`, `PostingVectorLimit=300`, others SPTAG defaults (`SearchInternalResultNum=64`, BKT head index) | tombstones; inserted/updated rows invisible until the nightly rebuild of buckets with ≥ 5 % changed rows |
| **SPFresh** (`Storage=FILEIO`) | **one** SPANN index for the whole window | same as above, plus SPTAG's SPFresh test settings (`InPlace`, `ReassignK=64`, `MergeThreshold=10`), `AllowZeroReplica=true`, `AppendThreadNum=16` (SPTAG's default of 1 couldn't keep up, see below), a 60 GB preallocated posting file | `AddIndexSPFresh` from 8 threads, `DeleteIndex`; daily maintenance = wait for the split/merge/reassign queue to drain, then `Checkpoint` |

**Why these SPANN settings.** At 1M rows (one bucket, 50 queries, the bench's seven filters) the head ratio decides the
pinned RAM (each head costs ~0.9 KB: the int8 head vector, its BKT/RNG graph entry, tree and ids):

| 1M sweep (`bench-results/*sweep*`) | Heads | Build | SPANN disk | R@24, broad filters | p50 | R@360 (5,400 cand), 20 % filters |
|---|---|---|---|---|---|---|
| SPTAG defaults (`Ratio` 0.1 → 14 % heads after SPTAG's thresholds), 8 replicas | 140,636 | 3.9 min | 3.9 KB/vector | 0.970–0.978 | 5–6 ms | 0.89–0.95 |
| same, 4 replicas | 140,713 | 3.9 min | 2.8 KB/vector | 0.970–0.976 | 5–6 ms | 0.89–0.94 |
| **3.5 % heads, 4 replicas, PVL 300** (used) | 34,892 | 1.1 min | 3.0 KB/vector | 0.953–0.964 | 3–4 ms | 0.78–0.89 |
| 2.3 % heads, 4 replicas, PVL 400 | 23,509 | 0.9 min | 2.9 KB/vector | 0.927–0.948 | 3.5–5 ms | 0.76–0.89 |

SPTAG's defaults (14 % heads) cost ~130 B/vector of RAM for the head index alone, three times the 43 B/vector budget,
for +0.01–0.02 recall. 3.5 % heads is the smallest setting that kept recall@24 near 0.95. Eight replicas (the
paper's setting) bought nothing over four on this data and cost 40 % more disk. In 768-d almost every vector qualifies
for all 8 replicas under SPANN's RNG rule ("Replica Count Dist: 8, 46611" of 54,904 in the build log), so postings hit
the size cap and replicas get cut.

`SelectHead.Ratio` alone does not set the head count: with SPTAG's default `SelectThreshold` 6 / `SplitThreshold` 25 /
`SplitFactor` 5, `Ratio` 0.03 still selected 14 % heads. They have to be set to 0 (derived from the ratio), as SPTAG's own
SPFresh test does.

### SPFresh layouts that failed

Two SPFresh setups failed before the one measured here. Both failures are in SPTAG's `FILEIO` path. Logs:
`bench-results/sptag-spfresh-*.log`.

1. **One SPFresh index per day bucket** (like the static layout). A new day's index has to be bootstrapped
   (`AddIndex` can't add to an empty index), here from its first 50,000 rows; then ~950,000 more rows arrive through
   `AddIndex`. LIRE's split thread fell behind and postings grew to 1,700–2,000 4-KB blocks (~9–10k vectors; the limit is
   317, and FILEIO's per-posting block table has 61 slots). One posting then held garbage vector ids
   (`Error vid in VersionLabel Delete check: 265947905. Max Allowed: 56851`), and its split failed and was retried
   153,313 times in three minutes. Seeding a SPANN index small and growing it 20× in a day is the worst case for LIRE,
   but the corruption is a defect, not a slowdown. Per-day buckets were dropped for SPFresh.
2. **One 10M index with SPTAG's default `AllowZeroReplica=false`.** After the posting cut, the build refills the
   vectors that lost all their replicas (89,653 of 10M here) by appending them to their nearest postings. In 768-d those
   are hub postings, so every refill append triggers a split, and splits ran at 3–4 per minute at the end, reading ~7 MB
   postings with `O_DIRECT`. Anon memory grew from 1.9 GB to 12 GB. It was aborted after 44 minutes, with no progress
   indicator and no failures logged. The measured run sets `AllowZeroReplica=true`, which skips the refill. Its postings
   then match a static build's: SPANN's static build drops zero-replica vectors too. At 1M rows the refill (410 vectors)
   finished.

SPFresh is therefore measured as **one long-lived index over the whole 10-day window**, which is also how the SPFresh
paper evaluates it: continuous inserts and deletes, with retention as `DeleteIndex` of the oldest day. Time filters
can't skip a whole index here, so the brute-force fallback uses 64k-slot time chunks.

### Equal memory

Every engine ran in a Linux container limited to **24 GB** (`--memory 24g --memory-swap 24g`, Docker Desktop WSL2
VM with 31 GB and 24 cores), on the same Docker virtual disk:

* SPTAG/SPANN and SPFresh: `madelene-sptag-server` in its container; the bench client (row generator, ground truth,
  query loop) on the Windows host over TCP (~0.1 ms per call).
* Madelene: the whole bench in one container (`deploy/madelene-bench/Dockerfile`, .NET 10 runtime), so its ground
  truth and row generation count against the same 24 GB.
* Madelene unconstrained: the same bench natively on Windows with the host's 64 GB, for comparison with the earlier
  documents.

"Anon" below is the cgroup's anonymous memory (heap, pinned indexes; what the kernel cannot evict). Page cache
(mapped files) is evictable and fills whatever the limit leaves.

## Results at 10M (static)

`--stream --rows 10000000 --days 10 --bucket-days 1 --queries 50 --ivf-min-rows 500000`, seed 7, the seven filters,
one shared ground-truth file. Times are client-side wall time. p50 first / p50 repeat / p99 repeat in ms.

Bold: recall below 0.95. "SPANN post-filter only" is the same SPANN index queried again without the brute-force
fallback. SPTAG/SPANN and SPFresh "first" passes ran after the calibration queries, so they are not cold. (An earlier
SPANN run without calibration had a cold first pass: 18.6 ms p50 for "tenant only", within 2 ms of its repeat pass on
the other filters.)

### Recall and latency, limit 24 / numCandidates 360

| Filter | Selectivity | Madelene R@24 | Madelene | SPTAG/SPANN R@24 | SPTAG/SPANN | SPANN post-filter only R@24 | SPANN post-filter only | SPFresh R@24 | SPFresh |
|---|---|---|---|---|---|---|---|---|---|
| tenant only | 100 % | 0.977 | 18.1 / 17.2 / 32.8 | 0.952 | 12.8 / 13.0 / 15.3 | 0.952 | 13.2 / 13.0 / 17.4 | **0.947** | 28.5 / 28.3 / 50.2 |
| + busiest source | 21.3 % | 0.983 | 9.3 / 9.5 / 17.4 | 0.959 | 17.4 / 17.2 / 19.3 | 0.959 | 15.1 / 15.0 / 18.2 | 0.952 | 35.4 / 35.8 / 66.8 |
| + mid source | 0.281 % | 0.994 | 8.9 / 8.5 / 33.3 | 0.992 | 34.6 / 34.2 / 82.4 | 0.973 | 23.4 / 23.8 / 27.0 | 0.992 | 240.9 / 245.4 / 251.7 |
| + 2 tokens (AND) | 28.4 % | 0.983 | 10.4 / 10.3 / 20.5 | **0.949** | 19.0 / 19.0 / 24.5 | **0.949** | 15.5 / 15.6 / 17.5 | **0.943** | 36.5 / 36.6 / 58.1 |
| + token OR + NOT | 20.8 % | 0.978 | 10.7 / 10.5 / 23.8 | 0.958 | 20.0 / 20.1 / 21.9 | 0.958 | 15.6 / 15.5 / 18.2 | 0.955 | 37.6 / 37.4 / 71.4 |
| + last 20 % of time | 20 % | 0.981 | 5.1 / 5.0 / 6.5 | 0.958 | 6.6 / 6.6 / 7.7 | 0.958 | 6.3 / 6.4 / 7.4 | 0.955 | 29.2 / 29.7 / 49.7 |
| + mid source + 5 % of time | 0.0139 % | 0.993 | 2.6 / 2.8 / 13.7 | 0.979 | 14.4 / 14.7 / 18.4 | **0.746** | 145.8 / 146.1 / 172.4 | 0.979 | 14.7 / 15.0 / 18.1 |

### limit 360 / numCandidates 5,400 (repeat pass)

| Filter | Madelene R@360 | p50 | SPTAG/SPANN R@360 | p50 | SPANN post-filter only R@360 | p50 | SPFresh R@360 | p50 |
|---|---|---|---|---|---|---|---|---|
| tenant only | 0.988 | 17.4 | 0.970 | 14.8 | 0.970 | 13.1 | 0.967 | 29.3 |
| + busiest source | 0.992 | 15.0 | 0.980 | 17.5 | 0.980 | 15.3 | 0.979 | 74.1 |
| + mid source | 0.989 | 10.9 | 0.979 | 36.6 | **0.774** | 281.4 | 0.979 | 245.4 |
| + 2 tokens (AND) | 0.990 | 14.9 | 0.977 | 19.6 | 0.977 | 15.9 | 0.975 | 63.8 |
| + token OR + NOT | 0.991 | 15.9 | 0.980 | 20.5 | 0.980 | 16.0 | 0.979 | 78.3 |
| + last 20 % of time | 0.991 | 7.9 | 0.980 | 6.6 | 0.980 | 6.6 | 0.978 | 66.5 |
| + mid source + 5 % of time | 0.995 | 3.1 | 0.989 | 14.5 | **0.372** | 146.5 | 0.989 | 15.3 |

### `exact: true` (filters matching ≤ 2M rows)

| Filter | Madelene R@24 | p50 / p99 | SPTAG/SPANN R@24 | p50 / p99 | SPFresh R@24 | p50 / p99 |
|---|---|---|---|---|---|---|
| + mid source | 0.994 | 7.3 / 19.3 | 0.992 | 30.4 / 73.9 | 0.992 | 244.5 / 251.9 |
| + last 20 % of time | 0.981 | 34.7 / 44.4 | 0.963 | 109.9 / 116.0 | 0.963 | 194.3 / 250.5 |
| + mid source + 5 % of time | 0.993 | 2.1 / 2.6 | 0.979 | 14.1 / 16.0 | 0.979 | 14.8 / 19.3 |

### Throughput (seven filters mixed, limit 24 / 360)

| Clients | Madelene QPS | p50 / p99 | SPTAG/SPANN QPS | p50 / p99 | SPFresh QPS | p50 / p99 |
|---|---|---|---|---|---|---|
| 1 | 100 | 9.5 / 23.6 | 51 | 17.5 / 78.5 | 17 | 36.7 / 253.0 |
| 4 | 293 | 12.4 / 32.5 | 121 | 30.9 / 78.8 | 63 | 38.3 / 249.0 |
| 16 | 433 | 34.6 / 98.9 | 130 | 114.3 / 315.6 | 179 | 53.6 / 356.3 |

* **Recall.** Madelene holds 0.977–0.994. SPANN and SPFresh sit at 0.94–0.96 on the broad filters. That is a ceiling,
  not a tuning gap: reading more posting lists stops helping (see calibration), and `exact: true` over the same int8
  vectors gets 0.963–0.992. SPTAG ranks by int8 distance with one global scale and no float rescoring; Madelene
  scales int8 per row and rescores its 1-bit candidates with them.
* **Selective filters need the brute-force fallback.** Post-filtering alone falls to 0.746 (0.014 % filter) and,
  at limit 360, 0.774 and 0.372. With the fallback, per-day SPANN reaches 0.979–0.992 at 14–35 ms where Madelene
  takes 3–9 ms. SPFresh reaches the same recall but takes 245 ms on the source filter (next point).
* **SPFresh's single index costs latency on broad filters** (28–38 ms vs 7–20 ms): it reads 256 posting lists of one
  big index; the per-day layout reads 64 per day bucket in parallel. Its 0.28 % source filter takes 245 ms. With 28k
  matching rows it is above the 20k brute-force threshold, so it is post-filtered through one 10M-row index. In
  per-day buckets each day has ~2.8k matches and goes to brute force.
* **Throughput.** Madelene: 433 QPS at 16 clients. Per-day SPANN saturates at ~130 QPS: every unfiltered query fans
  out to 10 indexes × 64 posting lists, read with `O_DIRECT`. SPFresh reaches 179 QPS at 16 clients, but its
  single-client p95 is ~245 ms because of the source filter above.
* **Madelene unconstrained (64 GB host) vs the 24 GB container:** the same recall to the third decimal, p50 within
  ±1.5 ms, 109/303/464 QPS vs 100/293/433. The limit doesn't bind Madelene at 10M.


## Mutable benchmark

`--mutable-days 4` (`tools/Madelene.Bench/MutableBench.cs`) runs after the static measurement, on the same 10M index,
with the same operations in the same order for every engine. The data is a rolling 10-day window. Each simulated day:

| Step | Rate (10M rows, 1M per day) | What happens |
|---|---|---|
| Retention | 1,000,000 rows (10 %) | delete everything older than the new window start (`DeleteOlderThan` / dropping a bucket / `DeleteIndex` per row) |
| Inserts | 1,000,000 rows (10 %) | the next day's rows, in time order |
| Updates | 200,000 ids (2 %) | random live rows get a new vector **from a random cluster** (an update that moves the vector) and new tokens; id, time, tenant and source stay |
| Deletes by id | 50,000 ids (0.5 %) | random live rows, distinct from the updated ones |

Inserts, updates and deletes are interleaved in 100 chunks. One client queries the whole time (the seven filters mixed,
limit 24 / 360) and its latency is reported as "during writes". After the stream, ground truth is recomputed over
the rows that are live now (current versions only), and recall@24 / p50 of all seven filters is measured twice:
before and after the engine's maintenance step. Time filters slide with the window. Maintenance:

* Madelene: `Flush` then `Compact()` (default thresholds: merge a bucket at 4 segments, rewrite a segment at 20 %
  deleted), as-is. Updates land in every day bucket, so every bucket gets new small segments each day and compaction
  rewrites all of them.
* SPTAG/SPANN: build the new day's bucket; rebuild every bucket whose tombstoned + pending rows exceed 5 % of it.
  Until then, updated and inserted rows are invisible and deleted ones are filtered out. That is the only update path
  static SPANN has.
* SPFresh: nothing to rebuild. Wait until LIRE's background split/merge/reassign queue is empty, then call
  `Index::Checkpoint()`, as SPTAG's own SPFresh driver (`inc/SPFresh/SPFresh.h`) does after every update batch. The
  checkpoint persists the block pool and, importantly, returns the blocks freed by splits and deletes to the free list.
  Without it the posting file only grows: in a run without checkpoints it went from 65 GB to 291 GB in four days
  (`bench-results/*append16-10000000x768-20261125-022721.json`).

Updates and deletes total 2.5 % per day. Inserts and retention are 10 % per day,
because a 10-day window at this scale turns over a tenth of its rows daily; at 3B rows with longer retention that share
would be smaller.

### Mutable results (10M, 4 simulated days, 24 GB each)

Ranges are over days 1–4 unless a day is named. Recall@24 at `numCandidates` 360. "Broad" means the five filters that
match ≥ 20 % of rows.

| | Madelene | SPTAG/SPANN (per-day, static) | SPFresh (one index) |
|---|---|---|---|
| Write stream per day (1M inserts, 200k updates, 50k deletes) | 23–25 s | 17–19 s (side tables only: nothing is indexed yet) | 384–631 s |
| Inserts / updates per second | 52–56k / 61–73k | 73–82k / 51–61k | 1.9–3.2k / 1.8–3.1k (8 writer threads) |
| Queries during writes, p50 / p99 | 21–22 / 82–101 ms | 18 / 43–74 ms | 64–108 / 628–997 ms |
| Recall before maintenance | 0.969–0.999 | **0.46–0.96**: new and updated rows invisible; broad filters 0.84–0.87, "last 20 % of time" 0.46–0.51 | 0.946–0.997 |
| Maintenance per day | `Compact`: 394–398 s, rewrites all 10 buckets | 337 s (day 1: new bucket + 4 rebuilds), 667–684 s (days 2–4: new bucket + 9 of 10 rebuilt) | day 1: 535 s to drain LIRE's queue; days 2–4: 1–2 s (the queue kept up); checkpoint 1.2–1.9 s |
| Recall after maintenance, day 1 → day 4 | 0.971–0.998 → 0.977–0.999 | 0.943–0.979 → 0.954–0.996 | 0.949–0.977 → 0.952–0.983 |
| Broad-filter p50 after maintenance, day 1 → day 4 | 4.9–17.6 → 5.1–17.5 ms | 6.9–20.9 → 6.9–21.8 ms | 50.5–60.0 → 69.5–79.0 ms (28–38 ms before the churn) |
| Data on disk, day 0 → day 4 | 9.5 → 9.4 GB | 39.1 → 38.4 GB | 75 → 198 GB (posting file 65 → 183 GB, of which in use 27.7 → 47.8 GB) |
| Anon RAM after day 4 | 10.8 GB (managed heap 0.14 GB) | 3.8 GB | 4.7 GB (peak 11.5 GB on day 1) |
| SPANN heads | — | 343k (rebuilt daily) | 356k → 455k (+28 %) |

**Madelene.** Writes are cheap: a tombstone bit, plus a row in the write buffer that is searched exactly, so results are
fresh at once and recall doesn't move. The cost is in maintenance, and in query latency before it. Updates touch every
day bucket, so each bucket gets small segments every day. Until compaction, broad queries took 31–52 ms p50 instead of
9–18 ms. Default compaction then rewrites all 10 buckets (6.6 min per day here, all cores). The index stays at
951 B/vector on disk.

The anon memory is the notable result. After the first compaction the process holds 7.8–10.9 GB of anonymous memory
while its managed heap is 0.14 GB. That is .NET's Server GC keeping the compaction buffers committed, not data. It
stays under the 24 GB limit, but for a 128 GB/3B budget it would need GC settings (`GCHeapHardLimit`,
`GCConserveMemory`) or smaller compaction batches. Not tuned here: the defaults are reported.

**SPTAG/SPANN.** The fastest writes, because nothing is indexed: rows go to the side tables and wait. Until the nightly
rebuild the index is stale. Recall on broad filters drops to 0.84–0.87, and on "last 20 % of time" to 0.46–0.51,
because the newest day isn't searchable at all. Each update counts twice against the 5 % rebuild threshold (a
tombstone in the old place, a pending row in the new one), so 2 % updates a day put 9 of 10 buckets over it every day.
Daily maintenance is therefore a near-full rebuild: 11 min at 10M on 24 cores, about 70 s per 1M rows.

**SPFresh.** Fresh at all times: recall before and after maintenance is identical every day, and it doesn't drift over
four days. LIRE pays for that in I/O:

* **Writes** are 16–40× slower than Madelene's: 1.9–3.2k inserts/s and 1.8–3.1k updates/s with 8 writer threads.
* **Background work.** With SPTAG's default single background thread (`AppendThreadNum=1`), day 1 left a queue of
  8.5M split/reassign jobs: about 6.8 jobs per write, mostly reassigning the vectors around each split. The queue then
  shrank by only 10–16k jobs/min, i.e. 9–14 hours to drain. That run was stopped
  (`bench-results/bench-stream-sptag-spfresh-single-b2-append1-10000000x768-partial.log`). With 16 threads it drained
  in 9 minutes, and on days 2–4 it kept up during the writes. The price: writes and queries slowed while it ran, and
  query p99 during writes was 0.6–1.0 s.
* **Postings collect garbage.** Deleted and superseded entries stay in posting lists until a split or merge rewrites
  them. Data in use grew from 27.7 GB to 47.8 GB in four days (+73 %) for the same number of live rows. Broad-filter
  latency went from 28–38 ms to 70–79 ms, and the head count grew 28 %.
* **Disk.** The posting file grows to its high-water mark and never shrinks. Blocks freed by splits are only reused
  after a `Checkpoint`, and day 1's burst pushed the file to 183 GB before the first checkpoint. Without checkpoints it
  grew by about 32 GB every day (291 GB after four days).
* **Layout.** Per-day buckets are not an option: seeding a small index and growing it 20× corrupted a posting list
  (see "SPFresh layouts that failed"). So one index serves all time ranges, and time filters rely on the server's
  brute-force path.

The server's side tables are not compacted in dynamic mode (they grew 7.7 → 11.4 GB). That is harness code. It is
counted in "data on disk" above but not in the posting-file numbers.

## RAM and disk per vector (measured at 10M)

| | Madelene | SPTAG/SPANN | SPFresh |
|---|---|---|---|
| Ingest of 10M rows | 91k rows/s (1.8 min) | 77k rows/s into side tables (2.2 min) | 77k rows/s (2.2 min) |
| Index build | compaction 6.3 min | 11.1 min (10 buckets) | 28.4 min (one index) |
| Disk: index | **951 B/vector** | SPANN files **2,965 B/vector** | posting file 6,510 B/vector allocated (60 GB preallocated); **2,770 B/vector in use** after the build, 4,860 B/vector after 4 days |
| Disk: what SPTAG doesn't store | — | +940 B/vector of server side tables: int8 copy for rebuilds and brute force (768), filter records (132), ids (32), id maps (8) | same |
| Anon after the build | — | 0.89 GB | 1.14 GB |
| Anon after queries | **0.58 GB = 58 B/vector** (managed heap 0.10 GB = 10 B/vector) | 3.52 GB = 352 B/vector | 1.96 GB = 196 B/vector |
| of which grows with rows | managed heap ~10 B/vector | head index 317 MB = **32 B/vector**, id map 268 MB = 27 B/vector, version map 1 B/vector | head index 324 MB = **32 B/vector** (414 MB = 42 B/vector after 4 days), FILEIO block table ~488 B per posting list ≈ 17–22 B/vector, id map 27 B/vector, version map 1 B/vector |
| of which fixed / per bucket | .NET runtime | SPTAG search workspaces: ~0.28 GB per bucket with 12 dedicated threads | search workspaces for one index |
| Peak RSS (VmHWM, includes mapped files) | 10.9 GB | 10.1 GB (build) | 11.1 GB (build) |

A head (one of SPANN's in-memory centroids) costs ~0.9 KB: its int8 vector plus its BKT tree and RNG graph entries
(`head_index_bytes`, from SPTAG's own `BufferSize()`). The id map is server code: open addressing, 16 B per slot,
sized to a power of two. It is needed because SPTAG addresses vectors by its own ids, and a delete or update by `_id`
has to find them.

## Extrapolation to 3B vectors (not measured)

Per-vector figures from the 10M runs, multiplied by 3B. Fixed costs are left out unless they scale with the number of
buckets.

| | Madelene | SPTAG/SPANN (per-day) | SPFresh (one index) |
|---|---|---|---|
| Disk | 951 B → **2.9 TB** | 2,965 B → 8.9 TB, +940 B of side tables → **11.7 TB** | 2,770–4,860 B in use → 8.3–14.6 TB, plus the file's high-water mark (3.8–4.5× the data in use here), plus side tables |
| Pinned RAM that grows with rows | heap ~7–10 B (10 B at 10M, 7 B at 100M) → **21–30 GB** | heads 32 B + id map 23–27 B + version map 1 B ≈ 56–60 B → **170–180 GB** | heads 32–42 B + block table 17–22 B + id map 23–27 B + version map 1 B ≈ 73–92 B → **220–275 GB** |
| Against 43 B/vector (128 GB) | fits | over, before any fixed cost; ~33 B/vector with the id map moved to disk | over even without the id map (50–65 B) |
| Fixed costs that grow with the layout | .NET compaction buffers (GC-retained, up to ~11 GB with 1M-row segments) | ~0.28 GB of search workspaces per day bucket: ~100 GB for 365 day buckets | none (one index), but one index means no time partitioning |
| Daily maintenance at 1 % churn of 3B | compaction of the touched buckets | rebuilding every touched bucket: ~70 s per 1M rows → ~58 h of 24-core time if updates touch all buckets | ~6.8 background jobs per write; 30M writes/day → ~200M jobs/day. Here 16 threads cleared 8.5M in ~9 min → ~3.5 h/day of 16-thread I/O |

Lowering SPANN's head ratio and moving the id map to disk would bring the SPANN layouts to ~22–40 B/vector. In the 1M
sweep, 2.3 % heads cost ~21 B/vector at recall@24 0.93–0.95. That leaves no room for fixed costs, and recall would
fall further below Madelene's.

## Verdict

**SPFresh does what it says: in-place updates, no rebuilds, no stale results.** Its recall stayed flat at 0.95
through four days of 10 % inserts, 10 % retention, 2 % vector-moving updates and 0.5 % deletes. Static SPANN can't do
that: it is stale until a nightly rebuild of nearly every bucket. Madelene gets the same freshness another way, with a
write buffer searched exactly plus tombstones, and at lower cost:

* **Recall.** Madelene 0.977–0.999. SPTAG and SPFresh 0.94–0.96 on broad filters. That is a ceiling of SPTAG's int8
  storage without rescoring, not of search depth.
* **Writes.** Madelene 52–73k/s. SPFresh 1.8–3.2k/s with 8 writers and 16 background threads, plus a day-1 backlog of
  8.5M jobs that would take 9–14 h to clear with SPTAG's default single background thread.
* **Latency over time.** SPFresh's broad-filter p50 went from 28–38 to 70–79 ms as postings collected garbage.
  Madelene's stayed at 5–18 ms after compaction.
* **RAM.** Neither SPTAG layout fits 43 B/vector at 3B. The head index alone is ~32 B/vector at the lowest head ratio
  that kept recall near 0.95. SPFresh adds a ~0.5 KB block table per posting list. Both need an id map that SPTAG
  doesn't provide.
* **Disk.** SPTAG/SPANN uses 3.1–4.1× Madelene's disk. SPFresh uses 2.9–5.1× in use, and more allocated.
* **Filtering.** SPANN has no filter. Everything this workload needs (time pruning, filter records, the fallback for
  selective filters, the id map) is server code around it, i.e. most of what Madelene already is. Post-filtering alone
  collapses on the selective filters (0.37–0.77).
* **SPTAG problems found on the way:**
  * a posting list corrupted when a small index grew fast;
  * the zero-replica refill effectively never finishes at 10M;
  * search workspaces are shared between indexes in one process;
  * a "mutable" search parameter crashed the server when raised;
  * the posting file never shrinks.

**Recommendation: keep Madelene.** One SPFresh idea is worth borrowing: local rebalancing. Split an oversized IVF list
and reassign only its neighbours, instead of rewriting whole segments. That would cut Madelene's daily compaction,
which here rewrote all 10 buckets every day (6.6 min at 10M) because updates touch every bucket. Madelene's recall
didn't drift over four days, so this would save maintenance cost, not fix quality. Separately, Madelene 0.1 left
~11 GB of committed heap behind after compaction; 0.2 returns it (`releaseMemoryAfterCompaction`).

## What was not run, and why

* **100M.** Neither SPTAG layout fits the 24 GB limit at 100M, by the 10M measurements:
  * SPFresh's single-index build peaked at 11 GB RSS for 10M rows (VmHWM), mostly build-time vectors and candidate
    lists that grow linearly: ~110 GB at 100M. It would also take over 4 hours to build at the measured 26–28 min per
    10M, before any update is measured.
  * Per-day SPANN at 100M means 100 buckets. With dedicated search threads (needed for correct results, see Setup)
    each bucket holds ~0.29 GB of SPTAG search workspaces: ~29 GB before the head indexes (3.2 GB) and id map (4 GB).
    Sharing threads across buckets would fit, but returns wrong results.

  Madelene at 100M is already measured (README; 0.7 GB heap, 952 B/vector). The 3B section extrapolates per vector
  instead.
* **SPFresh on SPDK** (the paper's setup). It needs an NVMe device handed to user space (uio/vfio), hugepages and Secure
  Boot off, i.e. a dedicated Linux host, not a Docker Desktop VM. The `FILEIO` numbers here go through ext4 in Docker's
  virtual disk with `O_DIRECT`, which is slower than SPDK on a raw NVMe. On a dedicated host, expect higher
  insert/update throughput and lower first-pass latency than measured here, but not different recall, RAM or disk.
* **SPTAG on Windows** and the **C#/Python bindings**: not needed; see Feasibility.
* **SPANN with SPTAG's default head ratio at 10M** (14 % heads). The 1M sweep shows the trade: ~+0.015 recall for 4×
  the head RAM (~130 B/vector), over the budget.

## Reproduce

```bash
docker build -t madelene-sptag deploy/sptag
docker build -f deploy/madelene-bench/Dockerfile -t madelene-bench .
docker run -d --name madelene-sptag --memory 24g --memory-swap 24g -p 7710:7710 -v madelene-sptag-data:/data madelene-sptag

B2="SelectHead.Ratio=0.03,SelectHead.SelectThreshold=0,SelectHead.SplitThreshold=0,SelectHead.SplitFactor=0,BuildSSDIndex.ReplicaCount=4,BuildSSDIndex.PostingVectorLimit=300"
COMMON="--stream --rows 10000000 --days 10 --bucket-days 1 --queries 50 --ivf-min-rows 500000 --seed 7 --mutable-days 4 --truth-dir <shared dir>"

# SPTAG/SPANN, one index per day
dotnet run -c Release --project tools/Madelene.Bench -- $COMMON --backend sptag --sptag-mode static --sptag-internal 64 --sptag-param "$B2"
# SPFresh, one index (fresh container)
dotnet run -c Release --project tools/Madelene.Bench -- $COMMON --bucket-days 3650 --backend sptag --sptag-mode dynamic --sptag-internal 256 \
  --sptag-param "$B2,BuildSSDIndex.StartFileSizeGB=60,BuildSSDIndex.GrowthFileSizeGB=10,BuildSSDIndex.AllowZeroReplica=true,BuildSSDIndex.AppendThreadNum=16"
# Madelene under the same limit, and unconstrained
docker run --rm --memory 24g --memory-swap 24g -v madelene-bench-data:/data -v <repo>/bench-results:/work/bench-results \
  -v <shared dir>:/truth madelene-bench $COMMON --dir /data/10m --truth-dir /truth
dotnet run -c Release --project tools/Madelene.Bench -- $COMMON
```

Tests: `dotnet test` (unit tests always; the end-to-end SPTAG test runs when `MADELENE_SPTAG=localhost:7710`).
