#!/usr/bin/env python3
"""Compute evaluation metrics from result CSVs + the orchestrator log.

Inputs (default paths under ``--eval-dir``):

- ``corpus_catalog.csv``         (categories + platform_file_id)
- ``smart_upload_results.csv``   (from eval_smart_upload.py)
- ``query_catalog.csv``          (query_id, query_text, query_type, relevant_file_ids)
- ``search_results.csv``         (from eval_semantic_search.py)
- ``backend.log``                (optional; orchestrator stdout containing
                                  ``[Embedding] FileId=... extract_ms=...``)

Outputs:

- ``metrics_summary.csv``         (one row per metric: metric, value)
- ``per_category_results.csv``    (smart-upload, per correct category)
- ``per_query_type_results.csv``  (search, per query_type AND endpoint, plus
                                   a ``query_type == "ALL"`` aggregate row per
                                   endpoint)

Search metrics use binary relevance:
- P@k = |{relevant in top k}| / k
- nDCG@10 follows the Jarvelin-Kekalainen formulation:
    DCG@k  = sum_{i=1..k} rel_i / log2(i+1)
    IDCG@k = sum_{i=1..min(k,R)} 1 / log2(i+1)
    nDCG@k = DCG/IDCG
- MRR = mean over queries of 1 / (rank of first relevant), 0 if none in top-k

Statistical significance: paired t-test (`scipy.stats.ttest_rel`) on per-query
P@10, semantic vs substring, restricted to queries that appear in BOTH endpoint
sets.

Latency: every line matching the regex
``[Embedding] ... extract_ms=N ... embed_ms=N ... upsert_ms=N ... total_ms=N``
contributes one observation; p50 / p95 / p99 / mean are reported per stage.
"""

from __future__ import annotations

import argparse
import logging
import math
import re
from pathlib import Path
from typing import Dict, List, Optional

import numpy as np
import pandas as pd
from scipy import stats


log = logging.getLogger("eval.metrics")


# ----------------------------- helpers -----------------------------

def _parse_ids(s: str | float | None) -> List[str]:
    if s is None or (isinstance(s, float) and math.isnan(s)):
        return []
    return [x for x in str(s).split("|") if x]


def _precision_at_k(ranked: List[str], relevant: set[str], k: int) -> float:
    if k <= 0:
        return 0.0
    top = ranked[:k]
    if not top:
        return 0.0
    return sum(1 for x in top if x in relevant) / float(k)


def _reciprocal_rank(ranked: List[str], relevant: set[str]) -> float:
    for i, x in enumerate(ranked, start=1):
        if x in relevant:
            return 1.0 / i
    return 0.0


def _dcg_at_k(ranked: List[str], relevant: set[str], k: int) -> float:
    s = 0.0
    for i, x in enumerate(ranked[:k], start=1):
        if x in relevant:
            s += 1.0 / math.log2(i + 1)
    return s


def _ndcg_at_k(ranked: List[str], relevant: set[str], k: int) -> float:
    dcg = _dcg_at_k(ranked, relevant, k)
    ideal_rel = min(len(relevant), k)
    if ideal_rel == 0:
        return 0.0
    idcg = sum(1.0 / math.log2(i + 1) for i in range(1, ideal_rel + 1))
    return dcg / idcg if idcg > 0 else 0.0


EMB_LOG_RE = re.compile(
    r"\[Embedding\].*?extract_ms=(?P<extract>\d+)"
    r".*?embed_ms=(?P<embed>\d+)"
    r".*?upsert_ms=(?P<upsert>\d+)"
    r".*?total_ms=(?P<total>\d+)"
)


def parse_embedding_log(path: Optional[Path]) -> Dict[str, List[int]]:
    """Extract per-stage timings from an orchestrator log file."""
    out: Dict[str, List[int]] = {
        "extract_ms": [], "embed_ms": [], "upsert_ms": [], "total_ms": [],
    }
    if not path or not path.is_file():
        return out
    with path.open("r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            m = EMB_LOG_RE.search(line)
            if not m:
                continue
            out["extract_ms"].append(int(m.group("extract")))
            out["embed_ms"].append(int(m.group("embed")))
            out["upsert_ms"].append(int(m.group("upsert")))
            out["total_ms"].append(int(m.group("total")))
    return out


# ----------------------------- main -----------------------------

def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--eval-dir", type=Path, default=Path("eval"))
    ap.add_argument(
        "--backend-log",
        type=Path,
        default=None,
        help="Backend log file containing [Embedding] timing lines. "
        "Default: <eval-dir>/backend.log if it exists.",
    )
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    eval_dir: Path = args.eval_dir
    summary: dict[str, float | int | str] = {}

    # ---------------- Smart-upload ----------------
    smart_path = eval_dir / "smart_upload_results.csv"
    if smart_path.is_file():
        smart = pd.read_csv(smart_path)
        n = len(smart)
        if n > 0:
            summary["smart_upload_n"] = int(n)
            summary["smart_upload_suggestion_fire_rate"] = float(smart["suggestion_fired"].mean())
            summary["smart_upload_top1_accuracy"] = float(smart["top1_match"].mean())
            summary["smart_upload_top3_accuracy"] = float(smart["top3_match"].mean())

            per_cat = (
                smart.groupby("correct_folder")
                .agg(
                    n=("file_id", "count"),
                    top1=("top1_match", "mean"),
                    top3=("top3_match", "mean"),
                    fire_rate=("suggestion_fired", "mean"),
                )
                .reset_index()
                .rename(columns={"correct_folder": "category"})
                .sort_values("category")
            )
            per_cat.to_csv(eval_dir / "per_category_results.csv", index=False)
            log.info("Wrote per_category_results.csv (%d categories)", len(per_cat))
    else:
        log.warning("Skipping smart-upload section: %s not found.", smart_path)

    # ---------------- Search ----------------
    queries_path = eval_dir / "query_catalog.csv"
    search_path = eval_dir / "search_results.csv"
    corpus_path = eval_dir / "corpus_catalog.csv"
    if queries_path.is_file() and search_path.is_file():
        queries = pd.read_csv(queries_path)
        search = pd.read_csv(search_path)

        # `query_catalog.csv` writes relevance judgements as corpus file_ids
        # (e.g. cooking_recipes_007), but the platform's search endpoints return
        # platform fileId GUIDs. Build a guid -> file_id map from the corpus
        # catalog and translate ranked GUIDs back to file_ids before comparing.
        platform_to_fileid: Dict[str, str] = {}
        if corpus_path.is_file():
            # keep_default_na=False prevents pandas from turning empty cells into
            # NaN (which would str()-cast to the literal "nan" and collide in the
            # map for every smart_upload_test row whose platform_file_id is empty).
            corpus = pd.read_csv(corpus_path, keep_default_na=False, dtype=str)
            for _, crow in corpus.iterrows():
                pid = str(crow.get("platform_file_id", "")).strip()
                fid = str(crow.get("file_id", "")).strip()
                if pid and fid:
                    platform_to_fileid[pid] = fid
            log.info("Built platform-id -> file-id map (%d entries)", len(platform_to_fileid))
        else:
            log.warning(
                "corpus_catalog.csv not at %s; search metrics will compare GUIDs "
                "directly to relevance judgements and almost certainly score 0.",
                corpus_path,
            )

        rel_by_qid: Dict[str, set[str]] = {
            str(row["query_id"]): set(_parse_ids(row.get("relevant_file_ids")))
            for _, row in queries.iterrows()
        }
        qtype_by_qid: Dict[str, str] = {
            str(row["query_id"]): str(row.get("query_type", "") or "unknown")
            for _, row in queries.iterrows()
        }

        per_query_rows: List[dict] = []
        unmapped_warned = False
        for _, row in search.iterrows():
            qid = str(row["query_id"])
            endpoint = str(row["endpoint"])
            ranked_raw = _parse_ids(row.get("ranked_file_ids"))
            # Translate platform GUIDs to corpus file_ids; anything we can't map
            # (e.g. files uploaded outside the corpus, or bootstrap files that
            # were never written back to corpus_catalog) is passed through
            # unchanged so the comparison still has a chance against GUID-style
            # judgements.
            ranked: List[str] = []
            for r in ranked_raw:
                mapped = platform_to_fileid.get(r, r)
                ranked.append(mapped)
                if mapped == r and platform_to_fileid and not unmapped_warned:
                    log.debug("First unmapped ranked id (will be passed through): %s", r)
                    unmapped_warned = True
            relevant = rel_by_qid.get(qid, set())
            per_query_rows.append(
                {
                    "query_id": qid,
                    "query_type": qtype_by_qid.get(qid, "unknown"),
                    "endpoint": endpoint,
                    "p_at_5": _precision_at_k(ranked, relevant, 5),
                    "p_at_10": _precision_at_k(ranked, relevant, 10),
                    "ndcg_at_10": _ndcg_at_k(ranked, relevant, 10),
                    "mrr": _reciprocal_rank(ranked, relevant),
                    "n_relevant": len(relevant),
                    "n_returned": len(ranked),
                }
            )
        per_query = pd.DataFrame(per_query_rows)

        if not per_query.empty:
            overall = (
                per_query.groupby("endpoint")
                .agg(
                    n_queries=("query_id", "count"),
                    p_at_5=("p_at_5", "mean"),
                    p_at_10=("p_at_10", "mean"),
                    ndcg_at_10=("ndcg_at_10", "mean"),
                    mrr=("mrr", "mean"),
                )
                .reset_index()
            )
            overall["query_type"] = "ALL"

            per_type = (
                per_query.groupby(["query_type", "endpoint"])
                .agg(
                    n_queries=("query_id", "count"),
                    p_at_5=("p_at_5", "mean"),
                    p_at_10=("p_at_10", "mean"),
                    ndcg_at_10=("ndcg_at_10", "mean"),
                    mrr=("mrr", "mean"),
                )
                .reset_index()
            )

            cols = ["query_type", "endpoint", "n_queries", "p_at_5", "p_at_10", "ndcg_at_10", "mrr"]
            combined = pd.concat([overall[cols], per_type[cols]], ignore_index=True)
            combined.to_csv(eval_dir / "per_query_type_results.csv", index=False)
            log.info("Wrote per_query_type_results.csv (%d rows)", len(combined))

            # Headline overall numbers go into the summary CSV.
            for _, r in overall.iterrows():
                ep = r["endpoint"]
                summary[f"search_{ep}_p_at_5"] = float(r["p_at_5"])
                summary[f"search_{ep}_p_at_10"] = float(r["p_at_10"])
                summary[f"search_{ep}_ndcg_at_10"] = float(r["ndcg_at_10"])
                summary[f"search_{ep}_mrr"] = float(r["mrr"])
                summary[f"search_{ep}_n_queries"] = int(r["n_queries"])

            # Paired t-test on per-query P@10 (semantic vs substring).
            sem = per_query[per_query["endpoint"] == "semantic"].set_index("query_id")["p_at_10"]
            sub = per_query[per_query["endpoint"] == "substring"].set_index("query_id")["p_at_10"]
            common = sem.index.intersection(sub.index)
            if len(common) >= 2:
                tstat, pval = stats.ttest_rel(sem.loc[common].values, sub.loc[common].values)
                summary["search_paired_n"] = int(len(common))
                summary["search_paired_t_stat"] = float(tstat)
                summary["search_paired_p_value"] = float(pval)
                summary["search_paired_p10_semantic_mean"] = float(sem.loc[common].mean())
                summary["search_paired_p10_substring_mean"] = float(sub.loc[common].mean())
                summary["search_paired_p10_mean_diff_sem_minus_sub"] = float(
                    (sem.loc[common] - sub.loc[common]).mean()
                )
            else:
                log.warning(
                    "Skipping paired t-test: only %d query_ids appear in both endpoints.",
                    len(common),
                )
    else:
        log.warning("Skipping search section: missing %s or %s.", queries_path, search_path)

    # ---------------- Latency ----------------
    log_path = args.backend_log or (eval_dir / "backend.log")
    latency = parse_embedding_log(log_path)
    total_samples = len(latency.get("total_ms", []))
    if total_samples > 0:
        log.info("Parsed %d [Embedding] timing lines from %s", total_samples, log_path)
        for stage, values in latency.items():
            arr = np.array(values, dtype=float)
            if arr.size == 0:
                continue
            summary[f"latency_{stage}_n"] = int(arr.size)
            summary[f"latency_{stage}_mean"] = float(arr.mean())
            summary[f"latency_{stage}_p50"] = float(np.percentile(arr, 50))
            summary[f"latency_{stage}_p95"] = float(np.percentile(arr, 95))
            summary[f"latency_{stage}_p99"] = float(np.percentile(arr, 99))
    else:
        log.warning(
            "No [Embedding] timing lines found in %s; latency metrics omitted.",
            log_path,
        )

    # ---------------- metrics_summary.csv ----------------
    summary_path = eval_dir / "metrics_summary.csv"
    pd.DataFrame([{"metric": k, "value": v} for k, v in summary.items()]).to_csv(
        summary_path, index=False
    )
    log.info("Wrote %s (%d metrics)", summary_path, len(summary))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
