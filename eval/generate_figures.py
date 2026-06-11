#!/usr/bin/env python3
"""Generate the three 300-DPI PNG figures for the eval report.

Reads:
- ``per_category_results.csv``    -> smart_upload_accuracy_by_category.png
- ``per_query_type_results.csv``  -> search_precision_by_query_type.png
- ``backend.log``                 -> latency_distribution.png

Run after ``compute_metrics.py``.
"""

from __future__ import annotations

import argparse
import logging
import re
import sys
from pathlib import Path
from typing import List

import numpy as np
import pandas as pd
import matplotlib

matplotlib.use("Agg")  # headless backend; no display required
import matplotlib.pyplot as plt


log = logging.getLogger("eval.figures")

EMB_TOTAL_RE = re.compile(r"\[Embedding\].*?total_ms=(?P<total>\d+)")


# ----------------------------- figure 1 -----------------------------

def smart_upload_figure(per_category_csv: Path, out_path: Path) -> None:
    if not per_category_csv.is_file():
        log.warning("Skipping smart-upload figure: %s not found", per_category_csv)
        return
    df = pd.read_csv(per_category_csv).sort_values("category")
    if df.empty:
        log.warning("Skipping smart-upload figure: %s is empty", per_category_csv)
        return

    x = np.arange(len(df))
    w = 0.4

    fig, ax = plt.subplots(figsize=(8.0, 4.5))
    ax.bar(x - w / 2, df["top1"], w, label="Top-1", color="#6d5dfc", edgecolor="white")
    ax.bar(x + w / 2, df["top3"], w, label="Top-3", color="#bdb6f8", edgecolor="white")
    ax.set_xticks(x)
    ax.set_xticklabels(df["category"], rotation=20, ha="right")
    ax.set_ylim(0.0, 1.05)
    ax.set_ylabel("Accuracy")
    ax.set_title("Smart-upload suggestion accuracy by category")
    ax.legend(loc="upper right")
    ax.grid(axis="y", linestyle=":", alpha=0.5)
    for i, (_, row) in enumerate(df.reset_index(drop=True).iterrows()):
        ax.text(i - w / 2, float(row["top1"]) + 0.02, f"{float(row['top1']):.2f}",
                ha="center", fontsize=8)
        ax.text(i + w / 2, float(row["top3"]) + 0.02, f"{float(row['top3']):.2f}",
                ha="center", fontsize=8)

    fig.tight_layout()
    fig.savefig(out_path, dpi=300)
    plt.close(fig)
    log.info("Wrote %s", out_path)


# ----------------------------- figure 2 -----------------------------

def search_precision_figure(per_query_type_csv: Path, out_path: Path) -> None:
    if not per_query_type_csv.is_file():
        log.warning("Skipping search-precision figure: %s not found", per_query_type_csv)
        return
    df = pd.read_csv(per_query_type_csv)
    df = df[df["query_type"] != "ALL"]
    if df.empty:
        log.warning("Skipping search-precision figure: no per-type rows in %s", per_query_type_csv)
        return

    types = sorted(df["query_type"].unique())
    endpoints = ["semantic", "substring"]
    colors = {"semantic": "#6d5dfc", "substring": "#bdb6f8"}

    x = np.arange(len(types))
    w = 0.4

    fig, ax = plt.subplots(figsize=(8.0, 4.5))
    for i, ep in enumerate(endpoints):
        vals = []
        for t in types:
            sub = df[(df["query_type"] == t) & (df["endpoint"] == ep)]
            vals.append(float(sub["p_at_10"].mean()) if not sub.empty else 0.0)
        ax.bar(x + (i - 0.5) * w, vals, w, label=ep.capitalize(),
               color=colors.get(ep, "#888"), edgecolor="white")
        for j, v in enumerate(vals):
            ax.text(x[j] + (i - 0.5) * w, v + 0.02, f"{v:.2f}", ha="center", fontsize=8)

    ax.set_xticks(x)
    ax.set_xticklabels(types, rotation=15, ha="right")
    ax.set_ylim(0.0, 1.05)
    ax.set_ylabel("P@10")
    ax.set_title("Search precision by query type (P@10, semantic vs substring)")
    ax.legend(loc="upper right")
    ax.grid(axis="y", linestyle=":", alpha=0.5)

    fig.tight_layout()
    fig.savefig(out_path, dpi=300)
    plt.close(fig)
    log.info("Wrote %s", out_path)


# ----------------------------- figure 3 -----------------------------

def latency_figure(backend_log: Path, out_path: Path) -> None:
    if not backend_log.is_file():
        log.warning("Skipping latency figure: %s not found", backend_log)
        return
    totals: List[int] = []
    with backend_log.open("r", encoding="utf-8", errors="ignore") as fh:
        for line in fh:
            m = EMB_TOTAL_RE.search(line)
            if m:
                totals.append(int(m.group("total")))
    if not totals:
        log.warning("Skipping latency figure: no [Embedding] lines in %s", backend_log)
        return

    arr = np.array(totals, dtype=float)
    p50 = float(np.percentile(arr, 50))
    p95 = float(np.percentile(arr, 95))
    p99 = float(np.percentile(arr, 99))
    nbins = max(8, min(40, int(arr.size / 5) or 8))

    fig, ax = plt.subplots(figsize=(8.0, 4.5))
    ax.hist(arr, bins=nbins, color="#6d5dfc", alpha=0.85, edgecolor="white")
    for val, label, color in [
        (p50, f"p50={p50:.0f} ms", "#2c2c2c"),
        (p95, f"p95={p95:.0f} ms", "#ff7f0e"),
        (p99, f"p99={p99:.0f} ms", "#d62728"),
    ]:
        ax.axvline(val, color=color, linestyle="--", linewidth=1.5, label=label)
    ax.set_xlabel("End-to-end embedding latency (ms)")
    ax.set_ylabel("Count")
    ax.set_title(f"Embedding latency distribution (n={arr.size})")
    ax.legend(loc="upper right")
    ax.grid(axis="y", linestyle=":", alpha=0.5)

    fig.tight_layout()
    fig.savefig(out_path, dpi=300)
    plt.close(fig)
    log.info("Wrote %s", out_path)


# ----------------------------- entrypoint -----------------------------

def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--eval-dir", type=Path, default=Path("eval"))
    ap.add_argument(
        "--backend-log",
        type=Path,
        default=None,
        help="Default: <eval-dir>/backend.log",
    )
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )

    eval_dir = args.eval_dir
    backend_log = args.backend_log or (eval_dir / "backend.log")

    smart_upload_figure(
        eval_dir / "per_category_results.csv",
        eval_dir / "smart_upload_accuracy_by_category.png",
    )
    search_precision_figure(
        eval_dir / "per_query_type_results.csv",
        eval_dir / "search_precision_by_query_type.png",
    )
    latency_figure(backend_log, eval_dir / "latency_distribution.png")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
