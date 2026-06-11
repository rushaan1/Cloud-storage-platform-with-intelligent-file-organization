"""Shared helpers for the AegisCloud evaluation harness.

Environment variables
---------------------
AEGIS_BASE_URL    Backend base URL (default: https://localhost:7219)
AEGIS_EMAIL       Account email to authenticate as.
AEGIS_PASSWORD    Account password.
AEGIS_SSE_PATH    SSE endpoint path (default: /api/Modifications/sse).
AEGIS_VERIFY_SSL  "true" to verify TLS certs (default: false for localhost dev).

`AegisClient` re-uses a single `requests.Session` so the JWT cookie set by
`/api/Account/login` is sent on every subsequent request. Call
`ensure_authenticated()` between operations and the session is re-logged-in
once `refresh_after_seconds` has elapsed since the last login.
"""

from __future__ import annotations

import json
import logging
import os
import time
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

import requests
import urllib3
from sseclient import SSEClient

urllib3.disable_warnings(urllib3.exceptions.InsecureRequestWarning)

BASE_URL = os.environ.get("AEGIS_BASE_URL", "https://localhost:7219").rstrip("/")
EMAIL = os.environ.get("AEGIS_EMAIL", "")
PASSWORD = os.environ.get("AEGIS_PASSWORD", "")
VERIFY_SSL = os.environ.get("AEGIS_VERIFY_SSL", "false").lower() == "true"

ACCOUNT_LOGIN_PATH = "/api/Account/login"
MODIFICATIONS_ADD_PATH = "/api/Modifications/add"
MODIFICATIONS_UPLOAD_PATH = "/api/Modifications/upload"
SSE_AUTH_PATH = "/api/Modifications/sseauth"
SSE_PATH = os.environ.get("AEGIS_SSE_PATH", "/api/Modifications/sse")
RETRIEVALS_SEMANTIC_SEARCH = "/api/Retrievals/semanticSearch"
RETRIEVALS_SUBSTRING_SEARCH = "/api/Retrievals/getAllFiltered"

# Default timeouts (seconds). Tuned for localhost dev; override on the client if
# you point at a remote backend with higher RTT.
DEFAULT_API_TIMEOUT = 60        # login / search / folder create / sseauth
DEFAULT_UPLOAD_TIMEOUT = 300    # POST /upload — large PDFs take a few seconds
SSE_CONNECT_TIMEOUT = 30        # only the TLS+headers part of the SSE GET

# All folder paths sent to the platform are RELATIVE to InitialPathForStorage;
# the AppendToPath model binder prepends `C:\CloudStoragePlatform`.
DEFAULT_HOME_PATH = "\\home"

logger = logging.getLogger("eval")


class AuthError(RuntimeError):
    """Raised when credentials are missing or login fails."""


class AegisClient:
    """Authenticated HTTP + SSE client for the platform."""

    def __init__(
        self,
        base_url: str = BASE_URL,
        email: str = EMAIL,
        password: str = PASSWORD,
        refresh_after_seconds: int = 45 * 60,
        verify_ssl: bool = VERIFY_SSL,
        api_timeout: float = DEFAULT_API_TIMEOUT,
        upload_timeout: float = DEFAULT_UPLOAD_TIMEOUT,
    ):
        self.base_url = base_url
        self.email = email
        self.password = password
        self.refresh_after_seconds = refresh_after_seconds
        self.api_timeout = api_timeout
        self.upload_timeout = upload_timeout
        # Main session: short-lived API calls (login, upload, search, sseauth).
        self.session = requests.Session()
        self.session.verify = verify_ssl
        # Dedicated session for the streaming SSE GET. Keeping it on a separate
        # urllib3 pool guarantees the long-lived stream cannot stall an
        # in-flight upload by hogging the main session's pool slot.
        self.sse_session = requests.Session()
        self.sse_session.verify = verify_ssl
        self._last_login_at: float = 0.0

    # ----------------------------- auth -----------------------------

    def login(self) -> Dict[str, Any]:
        if not self.email or not self.password:
            raise AuthError(
                "Set AEGIS_EMAIL and AEGIS_PASSWORD environment variables before running."
            )
        body = {"Email": self.email, "Password": self.password, "RememberMe": True}
        r = self.session.post(
            self.base_url + ACCOUNT_LOGIN_PATH, json=body, timeout=self.api_timeout
        )
        if r.status_code >= 400:
            raise AuthError(f"Login failed: HTTP {r.status_code}: {r.text[:300]}")
        self._last_login_at = time.time()
        logger.info("Logged in as %s", self.email)
        try:
            return r.json()
        except json.JSONDecodeError:
            return {}

    def ensure_authenticated(self) -> None:
        """Re-login if we've gone past the refresh window (default: 45 minutes)."""
        if self._last_login_at == 0.0 or (
            time.time() - self._last_login_at > self.refresh_after_seconds
        ):
            self.login()

    # ----------------------------- folders + upload -----------------------------

    def create_folder(self, parent_path: str, folder_name: str) -> Optional[Dict[str, Any]]:
        """Create a folder named `folder_name` under `parent_path`.

        Returns the FolderResponse, or None if the folder already exists (the
        platform throws DuplicateFolderException → HTTP 400 with a Problem body).
        """
        new_folder_path = parent_path.rstrip("\\") + "\\" + folder_name
        body = {"FolderName": folder_name, "FolderPath": new_folder_path}
        r = self.session.post(
            self.base_url + MODIFICATIONS_ADD_PATH, json=body, timeout=self.api_timeout
        )
        if r.status_code in (400, 409):
            return None
        r.raise_for_status()
        try:
            return r.json()
        except json.JSONDecodeError:
            return None

    def upload_file(
        self,
        local_path: Path,
        target_folder_path: str,
        part_of_folder_upload: bool = False,
    ) -> Dict[str, Any]:
        """Upload a single file. Returns the first FileResponse from the list
        the controller returns.

        `target_folder_path` is e.g. `\\home\\recipes` (no InitialPathForStorage
        prefix — the controller's `AppendToPath` binder adds it on its end).
        """
        file_name = Path(local_path).name
        target_file_path = target_folder_path.rstrip("\\") + "\\" + file_name
        url = (
            f"{self.base_url}{MODIFICATIONS_UPLOAD_PATH}"
            f"?partOfFolderUpload={'true' if part_of_folder_upload else 'false'}"
        )
        with open(local_path, "rb") as fh:
            payload = fh.read()
        # Order matters: the controller's manual multipart reader expects fileName,
        # then filePath, then the file section.
        files = [
            ("fileName", (None, file_name)),
            ("filePath", (None, target_file_path)),
            ("file", (file_name, payload, "application/octet-stream")),
        ]
        # (connect, read) timeout. Without this, a wedged TLS handshake or stuck
        # Kestrel connection keeps requests blocked forever — the script will
        # silently hang with no log output at all.
        r = self.session.post(url, files=files, timeout=(self.api_timeout, self.upload_timeout))
        r.raise_for_status()
        data = r.json()
        if isinstance(data, list):
            if not data:
                raise RuntimeError(f"Upload returned an empty list for {file_name}")
            return data[0]
        return data

    # ----------------------------- search -----------------------------

    def semantic_search(self, query: str, topK: int = 20, hybrid: bool = False) -> Dict[str, Any]:
        params = {"q": query, "topK": topK, "hybrid": "true" if hybrid else "false"}
        r = self.session.get(
            self.base_url + RETRIEVALS_SEMANTIC_SEARCH, params=params, timeout=self.api_timeout
        )
        r.raise_for_status()
        return r.json()

    def substring_search(self, query: str) -> Dict[str, Any]:
        params = {"searchString": query}
        r = self.session.get(
            self.base_url + RETRIEVALS_SUBSTRING_SEARCH, params=params, timeout=self.api_timeout
        )
        r.raise_for_status()
        return r.json()

    # ----------------------------- SSE -----------------------------

    def get_sse_token(self) -> str:
        """Fetch a single-use SSE token from /api/Modifications/sseauth.

        The token expires after ~1 minute and is consumed on the first /sse
        connection; callers must request a fresh one for each new stream.
        """
        r = self.session.get(self.base_url + SSE_AUTH_PATH, timeout=self.api_timeout)
        r.raise_for_status()
        body = r.json()
        token = body.get("sseToken") or body.get("SseToken")
        if not token:
            raise RuntimeError(f"sseauth returned no token: {body}")
        return token

    def open_sse(self) -> Tuple[requests.Response, SSEClient]:
        """Open an authenticated SSE stream.

        Returns the raw `requests.Response` (so the caller can `.close()` it)
        and a `SSEClient` ready for `.events()` iteration.

        Uses ``self.sse_session`` (separate from the main API session) so the
        long-lived stream cannot stall an in-flight upload. The /sse endpoint
        itself is [AllowAnonymous] and reads only the query-string token, so
        the SSE session does not need the auth cookie.
        """
        token = self.get_sse_token()
        url = f"{self.base_url}{SSE_PATH}?token={token}"
        # (connect, read): bounded TLS+headers, unbounded body (SSE blocks
        # waiting for events).
        resp = self.sse_session.get(
            url,
            stream=True,
            headers={"Accept": "text/event-stream", "Cache-Control": "no-cache"},
            timeout=(SSE_CONNECT_TIMEOUT, None),
        )
        if resp.status_code >= 400:
            try:
                msg = resp.text[:300]
            finally:
                resp.close()
            raise RuntimeError(f"SSE connect failed: HTTP {resp.status_code}: {msg}")
        return resp, SSEClient(resp)


# ----------------------------- response shape helpers -----------------------------

def file_response_id(file_response: Dict[str, Any]) -> Optional[str]:
    """Extract the file id from a FileResponse (camelCase by default in ASP.NET)."""
    return file_response.get("fileId") or file_response.get("FileId")


def file_response_path(file_response: Dict[str, Any]) -> Optional[str]:
    return file_response.get("filePath") or file_response.get("FilePath")


def bulk_response_files(body: Dict[str, Any]) -> List[Dict[str, Any]]:
    return body.get("files") or body.get("Files") or []


def bulk_response_folders(body: Dict[str, Any]) -> List[Dict[str, Any]]:
    return body.get("folders") or body.get("Folders") or []


# ----------------------------- logging -----------------------------

def configure_logging(verbose: bool = False) -> None:
    logging.basicConfig(
        level=logging.DEBUG if verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s | %(message)s",
        datefmt="%Y-%m-%d %H:%M:%S",
    )
