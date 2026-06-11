#!/usr/bin/env python3
"""Eval the smart-upload (folder_suggestion) flow.

For each row in corpus_catalog.csv with ``intended_use == "smart_upload_test"``:

1. Open a fresh SSE stream (the platform's sseauth token is single-use).
2. Upload the file to ``\\home`` (the catch-all root folder).
3. Listen for a ``folder_suggestion`` event whose ``content.fileId`` matches
   the uploaded file's id, up to ``--sse-timeout`` seconds.
4. Record top-3 suggested folder paths, scores, whether the event fired,
   and end-to-end latency from upload start to event receipt.

Writes ``eval/smart_upload_results.csv`` with columns::

    file_id, correct_folder,
    suggested_1, suggested_2, suggested_3,
    score_1, score_2, score_3,
    top1_match, top3_match,
    suggestion_fired, latency_ms

``correct_folder`` is the corpus row's ``category`` (e.g. "recipes"). Match is
computed against the leaf name of each ``suggested_*`` folder path.
"""

from __future__ import annotations

import argparse
import csv
import json
import logging
import sys
import time
from pathlib import Path
from threading import Event, Lock, Thread

sys.path.insert(0, str(Path(__file__).resolve().parent))
from common import (  # noqa: E402
    AegisClient,
    DEFAULT_HOME_PATH,
    configure_logging,
    file_response_id,
)


log = logging.getLogger("eval.smart_upload")


def _folder_basename(folder_path: str) -> str:
    """Leaf folder name from a Windows-style path like ``C:\\...\\home\\recipes``."""
    if not folder_path:
        return ""
    return folder_path.rstrip("\\").split("\\")[-1]


def main() -> int:
    ap = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    ap.add_argument("--catalog", type=Path, default=Path("eval/corpus_catalog.csv"))
    ap.add_argument("--out", type=Path, default=Path("eval/smart_upload_results.csv"))
    ap.add_argument(
        "--sse-timeout",
        type=int,
        default=300,
        help="Seconds to wait per file for a folder_suggestion event (default: 60).",
    )
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    configure_logging(args.verbose)

    with args.catalog.open("r", newline="", encoding="utf-8") as fh:
        rows = [
            r for r in csv.DictReader(fh) if r.get("intended_use") == "smart_upload_test"
        ]

    if not rows:
        raise SystemExit(
            "No rows with intended_use=='smart_upload_test'. Run select_test_set.py first."
        )

    client = AegisClient()
    client.login()

    log.info("Starting smart-upload eval over %d test file(s)", len(rows))

    results = []
    for idx, row in enumerate(rows, start=1):
        log.info("[%d/%d] %s (path=%s, correct=%s)",
                 idx, len(rows), row["file_id"], row["local_path"], row["category"])
        client.ensure_authenticated()
        correct = row["category"]
        local_path = Path(row["local_path"])

        result_row = {
            "file_id": row["file_id"],
            "correct_folder": correct,
            "suggested_1": "", "suggested_2": "", "suggested_3": "",
            "score_1": "", "score_2": "", "score_3": "",
            "top1_match": 0, "top3_match": 0,
            "suggestion_fired": 0, "latency_ms": "",
        }

        # Open SSE BEFORE upload so we don't miss a fast event. Buffer every
        # folder_suggestion event with its receive timestamp; we'll match by
        # fileId once we know it.
        log.info("  opening SSE stream...")
        try:
            sse_resp, sse_client = client.open_sse()
            log.info("  SSE stream open (HTTP %d)", sse_resp.status_code)
        except Exception as exc:
            log.error("%s SSE-open FAILED: %s", row["file_id"], exc)
            results.append(result_row)
            continue

        events_buffer: list[tuple[float, dict]] = []
        events_lock = Lock()
        stop = Event()

        def listener() -> None:
            try:
                for event in sse_client.events():
                    if stop.is_set():
                        return
                    if not event.data:
                        continue
                    try:
                        payload = json.loads(event.data)
                    except json.JSONDecodeError:
                        continue
                    if payload.get("eventType") != "folder_suggestion":
                        continue
                    content = payload.get("content") or {}
                    with events_lock:
                        events_buffer.append((time.time(), content))
            except Exception as exc:  # connection closed, etc.
                log.debug("listener exit (%s): %s", row["file_id"], exc)

        thread = Thread(target=listener, daemon=True)
        thread.start()

        # Upload
        log.info("  uploading %s to %s ...", local_path.name, DEFAULT_HOME_PATH)
        upload_start = time.time()
        try:
            response = client.upload_file(
                local_path, DEFAULT_HOME_PATH, part_of_folder_upload=False
            )
            target_file_id = file_response_id(response)
            print("TARGET FILE ID:", target_file_id)
            if not target_file_id:
                raise RuntimeError(f"upload returned no fileId: {response}")
            log.info("  upload OK in %d ms; platform fileId=%s; waiting for folder_suggestion (timeout=%ds)",
                     int((time.time() - upload_start) * 1000), target_file_id, args.sse_timeout)
        except Exception as exc:
            log.error("%s upload FAILED: %s", row["file_id"], exc)
            stop.set()
            # Same SSL-shutdown-hang protection as the success path below.
            def _detach_on_fail(resp):
                try:
                    resp.raw.close()
                except Exception:
                    pass
                try:
                    resp.close()
                except Exception:
                    pass
            Thread(target=_detach_on_fail, args=(sse_resp,), daemon=True).start()
            results.append(result_row)
            continue

        # Poll buffered events for a match, with timeout. Log each candidate
        # exactly once so we can see whether the equality check is firing.
        deadline = time.time() + args.sse_timeout
        matched: tuple[float, dict] | None = None
        seen_event_ids: set[int] = set()
        while time.time() < deadline:
            with events_lock:
                for ts, content in events_buffer:
                    key = id(content)
                    if key not in seen_event_ids:
                        cid = content.get("fileId")
                        log.info("  candidate fileId=%r target=%r match=%s",
                                 cid, target_file_id, cid == target_file_id)
                        seen_event_ids.add(key)
                    if content.get("fileId") == target_file_id:
                        matched = (ts, content)
                        break
            if matched is not None:
                break
            time.sleep(0.2)

        log.info("  poll loop ended; matched=%s", matched is not None)
        stop.set()
        # IMPORTANT: any flavour of close() on this response will block
        # indefinitely because the .NET endpoint is parked in
        # `await Task.Delay(-1)`. Python's ssl module sends a TLS close-notify
        # and waits for the peer's close-notify in reply, which never arrives.
        # We schedule the close on a daemon thread and DO NOT wait — the
        # connection leaks until the script exits and the OS reaps the socket;
        # the .NET SSE service prunes the dead client on its next broadcast.
        log.info("  scheduling background SSE close (do not wait)")

        def _detach_response(resp):
            try:
                resp.raw.close()
            except Exception:
                pass
            try:
                resp.close()
            except Exception:
                pass

        Thread(target=_detach_response, args=(sse_resp,), daemon=True).start()

        if matched is not None:
            ts, content = matched
            suggestions = (content.get("suggestions") or [])[:3]
            for i, s in enumerate(suggestions, start=1):
                result_row[f"suggested_{i}"] = s.get("folderPath", "")
                result_row[f"score_{i}"] = s.get("score", "")
            top_basenames = [
                _folder_basename(s.get("folderPath", "")) for s in suggestions
            ]
            result_row["top1_match"] = 1 if top_basenames and top_basenames[0] == correct else 0
            result_row["top3_match"] = 1 if correct in top_basenames else 0
            result_row["suggestion_fired"] = 1
            result_row["latency_ms"] = int((ts - upload_start) * 1000)
            log.info(
                "%s correct=%s top=%s top1=%d top3=%d latency_ms=%d",
                row["file_id"], correct,
                top_basenames[0] if top_basenames else "",
                result_row["top1_match"], result_row["top3_match"],
                result_row["latency_ms"],
            )
        else:
            log.info(
                "%s correct=%s suggestion did NOT fire within %ds",
                row["file_id"], correct, args.sse_timeout,
            )

        results.append(result_row)

    args.out.parent.mkdir(parents=True, exist_ok=True)
    with args.out.open("w", newline="", encoding="utf-8") as fh:
        writer = csv.DictWriter(
            fh,
            fieldnames=[
                "file_id", "correct_folder",
                "suggested_1", "suggested_2", "suggested_3",
                "score_1", "score_2", "score_3",
                "top1_match", "top3_match",
                "suggestion_fired", "latency_ms",
            ],
        )
        writer.writeheader()
        writer.writerows(results)

    n = len(results)
    fired = sum(1 for r in results if r["suggestion_fired"])
    top1 = sum(1 for r in results if r["top1_match"])
    top3 = sum(1 for r in results if r["top3_match"])
    log.info(
        "Wrote %s | files=%d fired=%d top1=%d top3=%d",
        args.out, n, fired, top1, top3,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
