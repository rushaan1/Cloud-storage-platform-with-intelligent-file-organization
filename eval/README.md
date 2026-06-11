# Evaluation harness

Reproducible scripts that drive the smart-upload (folder suggestion) and
semantic search features end-to-end against a running AegisCloud backend, then
compute the metrics + figures used in the academic write-up.

All scripts are plain Python 3.10+; install dependencies with:

```powershell
pip install -r eval\requirements.txt
```

## Required environment variables

```powershell
$env:AEGIS_BASE_URL  = "https://localhost:7219"   # default
$env:AEGIS_EMAIL     = "your-account@example.com"
$env:AEGIS_PASSWORD  = "your-password"
$env:AEGIS_VERIFY_SSL = "false"                    # default; flip to "true" for prod
$env:AEGIS_SSE_PATH  = "/api/Modifications/sse"   # default
```

`upload_bootstrap.py` and the eval scripts re-login automatically every
45 minutes so long runs survive JWT expiry.

## Pipeline / run order

Run each script from the **project root** (so `corpus/` and `eval/` are picked
up by relative path). All scripts share `eval/common.py` and read/write CSVs
in `eval/`.

| # | Command | Produces |
|---|---|---|
| 1 | `python eval\catalog_corpus.py` | `eval\corpus_catalog.csv` |
| 2 | `python eval\select_test_set.py --per-category 5 --seed 42` | (updates `corpus_catalog.csv`) |
| 3 | `python eval\upload_bootstrap.py` | (updates `corpus_catalog.csv` with `platform_file_id`) |
| 4 | **You** create `eval\query_catalog.csv` manually | `eval\query_catalog.csv` |
| 5 | `python eval\eval_smart_upload.py --sse-timeout 60` | `eval\smart_upload_results.csv` |
| 6 | `python eval\eval_semantic_search.py` | `eval\search_results.csv` |
| 7 | (while steps 5/6 run, redirect backend stdout to `eval\backend.log`) | `eval\backend.log` |
| 8 | `python eval\compute_metrics.py` | `metrics_summary.csv`, `per_category_results.csv`, `per_query_type_results.csv` |
| 9 | `python eval\generate_figures.py` | 3 × PNG at 300 DPI |

### Capturing backend timings (step 7)

The orchestrator emits a machine-parseable line on every successful embedding:

```
[Embedding] FileId=... extract_ms=143 embed_ms=312 upsert_ms=88 total_ms=574
```

`compute_metrics.py` and `generate_figures.py` parse these from a log file. The
easiest capture is to redirect the backend's stdout:

```powershell
dotnet run --project "Cloud Storage Platform\CloudStoragePlatform.Web.csproj" > eval\backend.log 2>&1
```

…and then run the eval scripts in another shell.

## What each script does

### `catalog_corpus.py`
Walks `corpus/<category>/...` (any depth) and writes one row per regular file.
Columns: `file_id` (e.g. `recipes_001`), `local_path`, `category`, `file_type`
(from extension), `intended_use` (`"bootstrap"` by default), `platform_file_id`
(empty until step 3).

### `select_test_set.py`
Randomly flips N files per category to `intended_use=smart_upload_test`.
**Fixed seed = reproducible selection.** Re-running with the same seed
re-selects the same files; re-running with a different seed first resets every
previous "smart_upload_test" back to "bootstrap" so the operation is idempotent.

### `upload_bootstrap.py`
1. Creates one folder per category under `\home` (silently skips duplicates).
2. For every row where `intended_use == "bootstrap"` and `platform_file_id`
   is empty, uploads to `\home\<category>` and writes the returned platform
   `fileId` back into `corpus_catalog.csv`.
3. Logs success / failure per file.

### `eval_smart_upload.py`
For every `smart_upload_test` row:

1. Opens a fresh SSE stream (`/api/Modifications/sseauth` → token →
   `/api/Modifications/sse?token=<>`).
2. Uploads the file to `\home` (intentionally to the catch-all root so the
   suggestion is non-trivial).
3. Listens for the first `folder_suggestion` event whose `content.fileId`
   matches the just-uploaded file (timeout `--sse-timeout`, default 60 s).
4. Writes top-3 suggested folder paths, scores, `top1_match`, `top3_match`,
   `suggestion_fired`, `latency_ms` to `smart_upload_results.csv`.

`top1_match` / `top3_match` compare the **leaf name** of each suggested
`folderPath` against the corpus row's `category` ("recipes", "invoices", …).

### `eval_semantic_search.py`
Reads `query_catalog.csv`:

| Column | Meaning |
|---|---|
| `query_id` | Stable id (e.g. `q001`) |
| `query_text` | Natural-language query the user would type |
| `query_type` | Free-form label (e.g. `recall`, `concept`, `paraphrase`); used to bucket metrics |
| `relevant_file_ids` | Pipe-separated **platform `fileId` GUIDs** (the `platform_file_id` column from `corpus_catalog.csv`) |

For each query it hits both:

- `GET /api/Retrievals/semanticSearch?q=<>&topK=20&hybrid=false`
- `GET /api/Retrievals/getAllFiltered?searchString=<>` (the existing substring search)

…and writes two rows to `search_results.csv` (one per endpoint) with
pipe-separated `ranked_file_ids`. The semantic row also has pipe-separated
`ranked_scores` — see the note below about synthetic scores.

> **Why synthetic scores?** The semantic endpoint returns the platform's
> `BulkResponse` shape which exposes rank order but not the underlying Pinecone
> cosine score. We emit a monotonically decreasing synthetic score (1.0,
> 1.0−1/N, …) so any downstream consumer that wants a score column has one;
> rank-based metrics (P@k, nDCG, MRR) are unaffected.

### `compute_metrics.py`
Reads everything above and writes:

- `metrics_summary.csv` — one row per metric (e.g. `smart_upload_top1_accuracy`,
  `search_semantic_p_at_10`, `latency_total_ms_p95`, …). The paired t-test
  result also lives here (`search_paired_t_stat`, `search_paired_p_value`).
- `per_category_results.csv` — smart-upload n / top1 / top3 / fire_rate per
  correct category.
- `per_query_type_results.csv` — search P@5 / P@10 / nDCG@10 / MRR per
  (`query_type`, `endpoint`), plus an `ALL` row per endpoint with the overall
  averages.

Metric definitions used (all binary-relevance):

- **P@k** = |relevant in top-k| / k
- **nDCG@10** (Järvelin-Kekäläinen): DCG@k = Σ rel_i / log₂(i+1) for i=1..k;
  IDCG@k = Σ 1 / log₂(i+1) for i=1..min(k, R); nDCG = DCG / IDCG.
- **MRR** = 1 / rank-of-first-relevant (0 if no relevant in top-k).
- **Significance**: `scipy.stats.ttest_rel` over per-query P@10
  (semantic vs substring), restricted to query_ids that appear in both
  endpoint result sets.
- **Latency**: every line matching
  `[Embedding] ... extract_ms=N ... embed_ms=N ... upsert_ms=N ... total_ms=N`
  contributes one observation; p50 / p95 / p99 / mean per stage.

### `generate_figures.py`
Three 300-DPI PNGs, all `Agg` backend (no display required):

1. `smart_upload_accuracy_by_category.png` — grouped bar of top-1 vs top-3 per
   category, with values labelled above bars.
2. `search_precision_by_query_type.png` — grouped bar of P@10 per query_type,
   semantic vs substring.
3. `latency_distribution.png` — histogram of total_ms with p50 / p95 / p99
   vertical dashed lines.

## Files / dataflow at a glance

| File | Written by | Read by |
|---|---|---|
| `corpus_catalog.csv` | `catalog_corpus.py` → `select_test_set.py` → `upload_bootstrap.py` | every other script |
| `query_catalog.csv` | **you (manually)** | `eval_semantic_search.py`, `compute_metrics.py` |
| `smart_upload_results.csv` | `eval_smart_upload.py` | `compute_metrics.py` |
| `search_results.csv` | `eval_semantic_search.py` | `compute_metrics.py` |
| `backend.log` | redirected from `dotnet run` | `compute_metrics.py`, `generate_figures.py` |
| `metrics_summary.csv` | `compute_metrics.py` | (your report) |
| `per_category_results.csv` | `compute_metrics.py` | `generate_figures.py` |
| `per_query_type_results.csv` | `compute_metrics.py` | `generate_figures.py` |

## Notes / caveats

- All folder paths sent to the platform are relative to `InitialPathForStorage`
  (the binder prepends `C:\CloudStoragePlatform`). Hence the scripts use
  `\home\<category>` everywhere, not the full `C:\CloudStoragePlatform\home\...`.
- The `sseauth` token is **single-use and short-lived**, so
  `eval_smart_upload.py` opens a fresh SSE stream per test file.
- For a clean apples-to-apples comparison, the semantic search is called with
  `hybrid=false` (pure semantic). Pass `--hybrid` if you want the production
  hybrid union behaviour instead.
- The default 5-files-per-category test set with seed 42 reproduces the numbers
  in the report. Vary `--per-category` or `--seed` to stress-test the result.
