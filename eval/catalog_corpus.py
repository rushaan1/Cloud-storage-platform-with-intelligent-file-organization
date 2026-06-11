#!/usr/bin/env python3
"""Walk a corpus directory and emit corpus_catalog.csv.

Layout expected::

    corpus/
        recipes/
            file1.pdf
            file2.txt
        invoices/
            ...

Output columns: file_id (e.g. recipes_001), local_path, category, file_type,
intended_use ("bootstrap" by default), platform_file_id ("").
"""

from __future__ import annotations

import argparse
import csv
import sys
from pathlib import Path
from typing import List, Tuple


def discover(corpus_dir: Path) -> List[Tuple[str, Path]]:
    """Return a sorted list of (category, file_path) for every regular file
    under corpus/<category>/.
    """
    if not corpus_dir.is_dir():
        raise SystemExit(f"corpus dir not found: {corpus_dir}")
    rows: List[Tuple[str, Path]] = []
    for category_dir in sorted(p for p in corpus_dir.iterdir() if p.is_dir()):
        for file_path in sorted(p for p in category_dir.rglob("*") if p.is_file()):
            rows.append((category_dir.name, file_path))
    return rows


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument(
        "--corpus-dir",
        type=Path,
        default=Path("corpus"),
        help="Path to the corpus directory (default: ./corpus).",
    )
    ap.add_argument(
        "--output",
        type=Path,
        default=Path("eval/corpus_catalog.csv"),
        help="Output CSV path (default: eval/corpus_catalog.csv).",
    )
    args = ap.parse_args()

    rows = discover(args.corpus_dir)
    if not rows:
        print(f"No files found under {args.corpus_dir}", file=sys.stderr)
        return 1

    args.output.parent.mkdir(parents=True, exist_ok=True)
    seq_per_category: dict[str, int] = {}
    with args.output.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.writer(fh)
        writer.writerow(
            ["file_id", "local_path", "category", "file_type", "intended_use", "platform_file_id"]
        )
        for category, path in rows:
            seq_per_category[category] = seq_per_category.get(category, 0) + 1
            file_id = f"{category}_{seq_per_category[category]:03d}"
            ext = path.suffix.lstrip(".").lower()
            writer.writerow([file_id, str(path), category, ext, "bootstrap", ""])

    total = sum(seq_per_category.values())
    print(
        f"Wrote {args.output} | categories={len(seq_per_category)} | files={total}"
    )
    for category, n in sorted(seq_per_category.items()):
        print(f"  {category}: {n}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
