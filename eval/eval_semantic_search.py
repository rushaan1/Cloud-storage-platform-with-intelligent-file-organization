#!/usr/bin/env python3
"""Run every query in query_catalog.csv against BOTH search endpoints.

Input ``eval/query_catalog.csv`` columns::

    query_id, query_text, query_type, relevant_file_ids

``relevant_file_ids`` is pipe-separated platform GUIDs (the
``platform_file_id`` column from ``corpus_catalog.csv``).

For every query this script issues two requests::

    GET /api/Retrievals/semanticSearch?q=<>&topK=20&hybrid=false
    GET /api/Retrievals/getAllFiltered?searchString=<>

and writes ``eval/search_results.csv`` with one row per (query, endpoint)::

    query_id, endpoint ("semantic" | "substring"),
    ranked_file_ids (pipe-separated),
    ranked_scores  (pipe-separated for semantic; empty for substring)

The semantic endpoint returns BulkResponse which does **not** include
per-result Pinecone scores — only rank order. For evaluation we emit a
synthetic monotonically-decreasing score (1.0, 1.0-1/N, ...) so any
downstream consumer that expects a score column still works; rank-based
metrics (P@k, nDCG, MRR) are unaffected.
"""

from __future__ import annotations

import argparse
import csv
import logging
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import (  # noqa: E402
    AegisClient,
    bulk_response_files,
    configure_logging,
    file_response_id,
)


log = logging.getLogger("eval.search")


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--queries", type=Path, default=Path("eval/query_catalog.csv"))
    ap.add_argument("--out", type=Path, default=Path("eval/search_results.csv"))
    ap.add_argument("--topk", type=int, default=20)
    ap.add_argument(
        "--hybrid",
        action="store_true",
        help="Use hybrid=true on the semantic endpoint (default: false, pure semantic).",
    )
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    configure_logging(args.verbose)

    if not args.queries.is_file():
        raise SystemExit(f"query catalog not found: {args.queries}")

    with args.queries.open("r", newline="", encoding="utf-8") as fh:
        reader = csv.DictReader(fh)
        queries = list(reader)

    if not queries:
        raise SystemExit(f"query catalog is empty: {args.queries}")

    client = AegisClient()
    client.login()

    out_rows = []
    for q in queries:
        client.ensure_authenticated()
        qid = q["query_id"]
        text = q["query_text"]

        # Semantic
        sem_ids: list[str] = []
        sem_scores: list[float] = []
        try:
            semantic = client.semantic_search(text, topK=args.topk, hybrid=args.hybrid)
            sem_files = bulk_response_files(semantic)
            sem_ids = [fid for fid in (file_response_id(f) for f in sem_files) if fid]
            n = max(len(sem_ids), 1)
            sem_scores = [round(1.0 - (i / n), 4) for i in range(len(sem_ids))]
        except Exception as exc:
            log.error("semantic FAILED qid=%s: %s", qid, exc)

        # Substring
        sub_ids: list[str] = []
        try:
            sub = client.substring_search(text)
            sub_files = bulk_response_files(sub)
            sub_ids = [fid for fid in (file_response_id(f) for f in sub_files) if fid]
        except Exception as exc:
            log.error("substring FAILED qid=%s: %s", qid, exc)

        out_rows.append(
            {
                "query_id": qid,
                "endpoint": "semantic",
                "ranked_file_ids": "|".join(sem_ids),
                "ranked_scores": "|".join(str(s) for s in sem_scores),
            }
        )
        out_rows.append(
            {
                "query_id": qid,
                "endpoint": "substring",
                "ranked_file_ids": "|".join(sub_ids),
                "ranked_scores": "",
            }
        )
        log.info("qid=%s sem=%d sub=%d", qid, len(sem_ids), len(sub_ids))

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(
            fh, fieldnames=["query_id", "endpoint", "ranked_file_ids", "ranked_scores"]
        )
        writer.writeheader()
        writer.writerows(out_rows)

    log.info("Wrote %s | queries=%d rows=%d", args.out, len(queries), len(out_rows))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
