#!/usr/bin/env python3
"""Upload every bootstrap-tagged corpus file to its category folder.

For every row in corpus_catalog.csv where ``intended_use == "bootstrap"`` and
``platform_file_id`` is empty, the file is uploaded to ``\\home\\<category>``.
The returned platform fileId is written back into the CSV so subsequent
scripts can use it for relevance judgements.

The category folders are created upfront under ``\\home`` (silently skipped
if they already exist). The session is re-logged-in every 45 minutes by
default so long runs don't fail on JWT expiry.
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
    DEFAULT_HOME_PATH,
    configure_logging,
    file_response_id,
)


log = logging.getLogger("eval.upload_bootstrap")


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument(
        "--catalog",
        type=Path,
        default=Path("eval/corpus_catalog.csv"),
        help="Path to the corpus catalog CSV.",
    )
    ap.add_argument(
        "--retry-failed",
        action="store_true",
        help="Re-attempt rows whose platform_file_id is empty even if they were "
        "previously attempted in a prior run.",
    )
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    configure_logging(args.verbose)

    if not args.catalog.is_file():
        raise SystemExit(f"catalog not found: {args.catalog}")

    with args.catalog.open("r", newline="", encoding="utf-8") as fh:
        reader = csv.DictReader(fh)
        rows = list(reader)
        fieldnames = reader.fieldnames or []

    client = AegisClient()
    client.login()

    # 1) Create one folder per category under \home.
    categories = sorted({row["category"] for row in rows})
    log.info("Creating %d category folder(s) under %s", len(categories), DEFAULT_HOME_PATH)
    for category in categories:
        try:
            result = client.create_folder(DEFAULT_HOME_PATH, category)
            if result is None:
                log.info("  /%s already exists (or duplicate-rejected); continuing", category)
            else:
                log.info("  created /%s", category)
        except Exception as exc:
            log.warning("  could not create /%s: %s", category, exc)

    # 2) Upload every bootstrap row that doesn't already have a platform_file_id.
    successes = 0
    failures = 0
    skipped = 0
    for row in rows:
        if row.get("intended_use") != "bootstrap":
            continue
        if row.get("platform_file_id") and not args.retry_failed:
            skipped += 1
            continue

        client.ensure_authenticated()
        local_path = Path(row["local_path"])
        target = DEFAULT_HOME_PATH + "\\" + row["category"]
        try:
            response = client.upload_file(local_path, target, part_of_folder_upload=False)
            fid = file_response_id(response)
            if not fid:
                raise RuntimeError(f"upload response had no fileId: {response}")
            row["platform_file_id"] = fid
            successes += 1
            log.info("uploaded %s -> %s (%s)", row["file_id"], fid, target)
        except Exception as exc:
            failures += 1
            log.error("FAILED %s: %s", row["file_id"], exc)

    # 3) Persist platform_file_id values.
    with args.catalog.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(fh, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)

    log.info(
        "Done. uploaded=%d failed=%d already-uploaded(skipped)=%d",
        successes, failures, skipped,
    )
    return 0 if failures == 0 else 2


if __name__ == "__main__":
    raise SystemExit(main())
