# Benchmarks

Every report here uses the same harness (`tools/Madelene.Bench`), the same synthetic CLIP-like data (seed 7: 1,000
clusters around a shared direction, text-like queries shifted by a modality gap, one tenant, 200 sources with Zipf
popularity, 18 tokens per row from a 500-word vocabulary), the same seven filters and the same exact ground truth.
Machine: one 24-core Windows 11 desktop, 64 GB RAM, local NVMe.

| Report | What it measures | Scale |
|---|---|---|
| [ARCHITECTURE.md § Performance](../ARCHITECTURE.md#12-performance) | Madelene itself, before and after the 0.2 planner and memory changes | 1M, 10M, 100M |
| [comparison-milvus.md](comparison-milvus.md) | Milvus 3.0: HNSW, IVF, RaBitQ, DiskANN and AiSAQ index types | 10M (100M extrapolated) |
| [comparison-qdrant.md](comparison-qdrant.md) | Qdrant 1.20: HNSW with int8 and binary quantization, ACORN filtering | 10M, 100M |
| [comparison-sptag-spfresh.md](comparison-sptag-spfresh.md) | SPTAG/SPANN and SPFresh, static and under four simulated days of churn, all engines in a 24 GB container | 10M |
| [fuzzy-and-text.md](fuzzy-and-text.md) | Non-exact filters (`$regex`, `$fuzzy`), BM25 full-text and hybrid search | 10M |

The comparison and full-text reports were measured on experimental branches; their backend code and raw result
files are not part of this package, and their command lines use benchmark options that only exist on those
branches. They are kept for their findings.

The comparisons were measured against **Madelene 0.1**, before the 0.2 planner and memory changes, so their
Madelene figures are 0.1's (for example 13–117 ms p50 and ~29 QPS at 100M, where 0.2 measures 4.7–21.3 ms and
112 QPS). Each report re-ran Madelene next to the other engine on a different day, so Madelene's own numbers vary
a little between reports (432–515 QPS at 10M and 16 clients).

`bench-results/` holds the raw reports for Madelene's own runs:

| Files | What |
|---|---|
| `bench-stream-100000000x768-20261126-*` | 100M, 0.1 against 0.2 ([ARCHITECTURE.md](../ARCHITECTURE.md#100m-vectors-01-against-02), README) |
| `bench-stream-100000000x768-20261111-*` | 100M, the first 0.1 run (cited by the Milvus and Qdrant reports) |
| `bench-1000000x768-*` | 1M, one IVF segment (README) |
| `memory-after-compaction-10m-*.txt` | memory after compacting 10M rows, 0.1 against 0.2 (CHANGELOG) |
| `bench-3000000x768-*`, `bench-stream-2000000x768-*` | development runs, not cited |
| `bench-50000x768-*` | early development runs with six of the seven filters, taken while the code was still changing; not comparable with each other or with later runs |

The comparisons reflect the versions, configurations, hardware and dates stated in each report, measured against
one target (billions of filtered vectors on a single machine with ≤ 128 GB RAM). They are not general verdicts on
those systems, which are designed for other deployments and may do better with other settings or newer releases.
Corrections are welcome: open an issue with the setting or result you think is wrong.

## The seven filters

| Filter | Rows matched |
|---|---|
| tenant only | 100 % |
| + busiest source | 21.3 % |
| + mid source | 0.28 % |
| + 2 tokens (AND) | 28.4 % |
| + token OR + NOT | 20.8 % |
| + last 20 % of time | 20 % |
| + mid source + 5 % of time | 0.014 % |

Each filter runs 50 queries at limit 24 / `numCandidates` 360 and at limit 360 / 5,400, a first and a repeat
pass, `exact: true` on filters matching ≤ 2M rows, then a 20 s load test per client count (1, 4, 16) mixing all
seven filters.

## Caveats

* The data is synthetic. It is built to be hard (thin margins between the best matches), but recall on real
  embeddings must be measured with real vectors (`--vectors`, `--query-vectors`).
* Memory limits were not equal across the earlier comparisons: Madelene ran in the host process, Milvus had
  ~14 GB, Qdrant a 24 GB container. The SPTAG/SPFresh comparison put every engine, Madelene included, in the same
  24 GB container.
* The 100M index (95 GB) is larger than the page cache, so "first" passes read from disk and "repeat" passes
  mostly don't.
