#!/usr/bin/env python3
"""Randomly mark N files per category as smart_upload_test in corpus_catalog.csv.

The CSV is rewritten in place. The same seed always produces the same selection,
and rerunning with a new seed first resets every prior "smart_upload_test" row
back to "bootstrap" so the operation is idempotent under seed change.
"""

from __future__ import annotations

import argparse
import csv
import random
from collections import defaultdict
from pathlib import Path


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument(
        "--catalog",
        type=Path,
        default=Path("eval/corpus_catalog.csv"),
        help="Path to the corpus catalog CSV (default: eval/corpus_catalog.csv).",
    )
    ap.add_argument(
        "--per-category",
        type=int,
        default=5,
        help="Number of files per category to flag as smart_upload_test (default: 5).",
    )
    ap.add_argument(
        "--seed",
        type=int,
        default=42,
        help="Random seed for reproducibility (default: 42).",
    )
    args = ap.parse_args()

    if not args.catalog.is_file():
        raise SystemExit(f"catalog not found: {args.catalog}")

    with args.catalog.open("r", newline="", encoding="utf-8") as fh:
        reader = csv.DictReader(fh)
        rows = list(reader)
        fieldnames = reader.fieldnames or []

    # Reset to bootstrap so re-runs with a different seed don't accumulate.
    for row in rows:
        if row.get("intended_use") == "smart_upload_test":
            row["intended_use"] = "bootstrap"

    by_category = defaultdict(list)
    for i, row in enumerate(rows):
        by_category[row["category"]].append(i)

    rng = random.Random(args.seed)
    selected_summary: dict[str, int] = {}
    for category, idxs in by_category.items():
        n = min(args.per_category, len(idxs))
        chosen = rng.sample(idxs, n)
        for idx in chosen:
            rows[idx]["intended_use"] = "smart_upload_test"
        selected_summary[category] = n

    with args.catalog.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    print(f"Updated {args.catalog} (seed={args.seed}, per_category={args.per_category})")
    for category, n in sorted(selected_summary.items()):
        print(f"  {category}: {n} files flagged smart_upload_test")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
