# Non-exact filters, full-text and hybrid search: measurements

> Measured on a separate experimental branch of Madelene (the fuzzy and full-text work). The backend code, deployment files and raw result files it mentions are not part of this package; this report is included for its findings. The Madelene figures in it are version 0.1, and its command lines use options of that branch's benchmark tool that the one in this repo doesn't have. See [the benchmark index](README.md).


Design and semantics are in that branch's design notes (not included). This page has the 10M benchmark and the
correctness checks behind it.

## Setup

Same machine and synthetic data as the README's runs (24 cores, 64 GB RAM, local NVMe; CLIP-like 768-d vectors,
1,000 clusters, text-like queries with a modality gap). `--stream --rows 10000000 --days 10 --queries 50
--ivf-min-rows 500000`, seed 7: ten daily 1M-row IVF segments after compaction, one tenant, 200 sources
(Zipf 1.1), 18 tokens per row from `tok0..tok499` (Zipf 1.0). Vector queries use limit 24 / 360 candidates.
"repeat" is the second run of each query (files cached); "first" numbers are in the JSON reports.

With `--text`, each row also gets a caption: 6–14 words drawn with Zipf 1.0 from a 20,000-word vocabulary of
syllable pseudo-words (`kavehi`, `lomira`, ...), so typos land near real words. Text queries: one common word
(Zipf rank < 50, in 1–10% of captions) plus 1–2 mid-frequency words (ranks 50–3,000); the fuzzy variants replace
one letter in every word. Index: one `text` field, `standard` analyzer, no stop words.

Three runs:

| Run | Binary | Index | Report |
|---|---|---|---|
| 1 | `main` | built, kept | `bench-stream-10000000x768-20261113-101341-main-baseline.json` |
| 2 | this branch | run 1's index reused (`--reuse`); once with the 7 non-exact scenarios, once without | `...-101608-branch-nonexact.json`, `...-103824-branch-plain.json` |
| 3 | this branch | new index with captions (`--text`) | `...-104938-branch-text.json` |

Correctness is checked against brute force in every run: exact float32 top-k over the rows the reference filter
evaluator accepts (vector), and a streaming brute-force BM25 over all 10M captions with the same statistics and
expansion rules (text).

## Summary

| Query type | Matching rows | Recall / correctness | p50 | p95 | p99 |
|---|---|---|---|---|---|
| Vector, exact filters (7 scenarios, unchanged) | 0.014–100% | 0.977–0.994, identical to `main` | 2.2–14.8 ms | 3.3–21.4 ms | 5.9–28.6 ms |
| Vector + prefix filter `^tok42` (11 terms) | 14.8% | 0.982 | 7.8 ms | 10.1 ms | 30.8 ms |
| Vector + prefix filter `^tok1` (111 terms) | 99.9% | 0.977 | 34.0 ms | 53.4 ms | 59.4 ms |
| Vector + regex `7$` (unanchored, 50 terms) | 82.7% | 0.974 | 19.6 ms | 38.8 ms | 41.6 ms |
| Vector + regex `/^TOK3[0-4]$/i` (5 terms) | 40.9% | 0.976 | 10.3 ms | 31.4 ms | 34.0 ms |
| Vector + `$fuzzy` tok123~1 (27 terms) | 71.5% | 0.982 | 17.2 ms | 34.1 ms | 43.6 ms |
| Vector + `$fuzzy` src50~1 (21 sources) | 30.7% | 0.979 | 9.2 ms | 24.3 ms | 35.8 ms |
| Vector + (prefix OR fuzzy) AND NOT regex | 0.036% | 0.988 | 12.1 ms | 35.1 ms | 37.1 ms |
| BM25 top-24, 2–3 words, `any` | 688k | 1.000 (max score error 6e-8) | 1.9 ms | 5.3 ms | 14.8 ms |
| BM25 top-24, `matchCriteria: all` | 639 | 1.000 | 1.7 ms | 5.3 ms | 16.6 ms |
| BM25 + source filter (0.28%) | 1,938 | 1.000 | 1.2 ms | 4.1 ms | 13.9 ms |
| BM25 + last 20% of time | 138k | 1.000 | 2.7 ms | 4.5 ms | 10.6 ms |
| BM25 fuzzy (1 typo per word, maxEdits 1) | 705k | 1.000 | 3.4 ms | 9.1 ms | 14.9 ms |
| BM25 fuzzy + busiest source (21%) | 150k | 1.000 | 3.9 ms | 9.7 ms | 17.3 ms |
| Hybrid `$rankFusion` (vector 50 + BM25 50 → top 24) | 688k | 0.954 vs exact fusion; 0.991 given the text list | 15.7 ms | 25.9 ms | 33.7 ms |
| Hybrid + last 20% of time | 138k | 0.990 | 4.4 ms | 10.5 ms | 20.5 ms |

BM25 "recall" is tie-aware: a hit counts if its true score reaches the true k-th score (BM25 over short captions
has many exact ties). Hybrid recall is measured against the fusion of the exact vector top-50 and the exact BM25
top-50; the number in the second column replaces the exact BM25 list by Madelene's own (exact, but tie-broken
differently), which isolates the approximate vector side.

## Unchanged exact-filter vector queries (regression check)

Same index, same queries, `main` vs this branch (run 1 vs run 2), limit 24 / 360 candidates, repeat pass:

| Filter | Recall main / branch | p50 main / branch | p99 main / branch |
|---|---|---|---|
| tenant only | 0.977 / 0.977 | 15.4 / 14.8 ms | 32.5 / 28.6 ms |
| + busiest source | 0.983 / 0.983 | 7.7 / 7.1 ms | 24.3 / 15.4 ms |
| + mid source | 0.994 / 0.994 | 6.3 / 6.1 ms | 29.2 / 24.5 ms |
| + 2 tokens (AND) | 0.983 / 0.983 | 9.1 / 8.6 ms | 34.1 / 26.4 ms |
| + token OR + NOT | 0.978 / 0.978 | 9.1 / 8.4 ms | 32.9 / 25.2 ms |
| + last 20% of time | 0.981 / 0.981 | 4.7 / 4.6 ms | 7.4 / 5.9 ms |
| + mid source + 5% of time | 0.993 / 0.993 | 2.1 / 2.2 ms | 11.6 / 18.7 ms |

Limit 360 / 5,400 candidates: recall identical in all seven scenarios, p50 0.1–1.3 ms lower on the branch.
Throughput over the same seven-scenario mix: 118 / 310 / 515 QPS (`main`, 1 / 4 / 16 clients) vs
126 / 323 / 555 QPS (branch). The differences are run-to-run noise; the vector path does the same work (the only
additions on it are one filter-tree walk and an empty expansion context).

The index with a caption field (run 3) answers the same seven vector queries with the same recall and p50
2.1–15.1 ms: a text index in the segment does not slow vector search.

Run 2's own throughput figures (80 / 216 / 401 QPS) are lower only because its query mix also includes the seven
non-exact scenarios, which match more rows.

## Non-exact filters: expansion

Per query, summed over the ten segments (each segment has its own dictionary; the write buffer was empty):

| Operator | Distinct terms | Dictionary entries visited | Filter time |
|---|---|---|---|
| prefix `^tok42` | 11 | 110 (11 per segment: the range itself) | 2.3 ms |
| prefix `^tok1` | 111 | 1,110 | 21.6 ms |
| regex `7$` | 50 | 5,000 (every `tags` term) | 8.7 ms |
| regex `/^TOK3[0-4]$/i` | 5 | 5,000 (case-insensitive: no prefix narrowing) | 2.5 ms |
| `$fuzzy` tok123, 1 edit | 27 | 2,700 (of 5,000) | 6.8 ms |
| `$fuzzy` src50, 1 edit | 21 | 1,200 (of 2,000) | 2.2 ms |
| fuzzy text, 1 edit per word, 20,000-word vocabulary | 3.4 terms per query | 3,923 (of ~200,000: 10 dictionaries × ~20,000 terms) | 1.6 ms planning |

Filter time is dominated by OR-ing posting lists, not by the expansion: `^tok1` matches 111 tokens, most of which
are dense (stored as bitsets), so it ORs 111 × 1M-bit bitsets per segment. The dictionary walk itself is
microseconds at this vocabulary size. With a small, shallow vocabulary (`tok0..tok499` all share `tok`) the fuzzy
walk can only prune after the third character and visits about half the dictionary; on the 20,000-word caption
vocabulary it visits under 1% of each dictionary per query word. Repeated queries hit the per-segment expansion cache.

## Text index cost

| | Without text (run 1) | With a caption field (run 3) |
|---|---|---|
| Disk | 951 B/vector | 978 B/vector |
| Text files only | — | **26.2 B/vector** (0.26 GB at 10M) |
| Ingest | 94k rows/s | 65k rows/s |
| Compaction (40 → 10 segments) | 6.1 min | 6.4 min |
| Managed heap after open | 0.10 GB | 0.10 GB |

26 B/vector for ~10 words: about 2.5 B per posting (varint row delta + term frequency), 2 B of field length per
row, and the per-segment dictionary (~20k terms × ~20 B per 1M rows, 0.4 B/row). At 3B vectors that is ~79 GB of disk per text
field of this size.

**RAM.** Nothing per row or per term is resident: the text dictionary, postings and length column are memory
mapped. Per segment, RAM holds the text meta (per field: path, documents, tokens) and two live counters per field,
a few hundred bytes. At 3B vectors in ~3,000 one-million-row segments that is under 1 MB in total, i.e. ~0 B/vector.
The bounded caches add at most 16 MB process-wide (filter expansions) plus ≤ 1,024 live-df entries (~40 KB) per
segment that has deletes. The write buffer holds the analyzed terms of buffered rows (≈ 0.5–1 KB per row for 10 words,
bounded by `sealRows`), on top of its 3 KB float32 vector per row.

**Ingest.** Analysis runs in `Prepare` on the writer's thread, which costs a third of the single-writer ingest rate
here. Hosts that need the rate can analyze in parallel before calling `Insert`/`Upsert` (a natural follow-up: accept
pre-analyzed rows).

## Text latency: where it goes

BM25 decodes every posting of the query's terms in every segment (here ~690k postings for 2.4 terms, 1.8 ms of scan
time across ten segments in parallel), then drops rows outside the filter. A selective filter therefore saves
scoring but not decoding, which is why `bm25 + mid source` (0.28% of rows) costs 1.2 ms rather than microseconds.
Skip lists or block-max WAND would cut both; they are not implemented on that branch.
Fuzzy queries add 1.4–1.6 ms of planning (expanding each word over 10 dictionaries plus statistics).

Hybrid latency is the slower of its two pipelines (they run in parallel): here the vector pipeline at limit 50 over
all 10M rows (≈15 ms); with a time filter both sides shrink to ~4 ms.

## Agreement with Atlas Search

`MongoTextIntegrationTests` (opt-in, `MADELENE_MONGO_URI`) loads 2,000 captioned documents into a throwaway
`madelene_it_*` database on a MongoDB Atlas local deployment, syncs them into Madelene, creates an Atlas Search
index with `lucene.standard` on the same field, and runs the same `text` queries on both:

```
one word                     fuzzy=False: overlap 18/20, score ratio ours/Atlas 1.000..1.000
two words                    fuzzy=False: overlap 20/20, score ratio 1.000..1.000
three words                  fuzzy=False: overlap 20/20, score ratio 1.000..1.000
three words, typo in each    fuzzy=True:  overlap 19/20, score ratio 1.000..1.000
three words, typo in each    fuzzy=True:  overlap 19/20, score ratio 1.000..1.000
```

Scores agree to three decimals, fuzzy ones included; the missing ids are ties at the 20th place, ordered differently.

## Reproduce

```bash
dotnet run -c Release --project tools/Madelene.Bench -- --stream --rows 10000000 --days 10 --queries 50 --ivf-min-rows 500000 --dir D:/bench/10m --keep
dotnet run -c Release --project tools/Madelene.Bench -- --stream --text --rows 10000000 --days 10 --queries 50 --ivf-min-rows 500000 --dir D:/bench/10m-text
```

`--no-nonexact` drops the seven non-exact filter scenarios (so the throughput mix matches older runs),
`--caption-vocab` sets the caption vocabulary, `--min-free-gb` (default 80) aborts when the index drive runs low.
