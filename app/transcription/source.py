"""SSRF-safe HTTPS connection and streaming download for customer media."""

from __future__ import annotations

import http.client
import math
import socket
import ssl
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
from pathlib import Path
from urllib.parse import parse_qs, urljoin, urlsplit

from app.transcription.paths import (
    InvalidSourceURL,
    resolve_public_addresses,
    validate_source_url_syntax,
)


MAX_MEDIA_SIZE_BYTES = 5 * 1024**3
CONNECT_TIMEOUT_SECONDS = 10
READ_TIMEOUT_SECONDS = 60
MAX_REDIRECTS = 5
TRANSIENT_HTTP_STATUSES = frozenset({408, 429, *range(500, 600)})


class MediaSizeExceeded(ValueError):
    """The origin reports or streams media above the configured maximum."""


class SourceUnavailable(RuntimeError):
    """The source is permanently unavailable or rejects a request."""


class ExpiredSourceURL(SourceUnavailable):
    """The source URL is known to have expired before a request could start."""


class TransientSourceFailure(SourceUnavailable):
    """A network or HTTP failure eligible for a bounded retry."""

    def __init__(self, retry_after_seconds: float | None = None) -> None:
        super().__init__()
        self.retry_after_seconds = retry_after_seconds


@dataclass(frozen=True)
class ConnectionStarted:
    content_length: int | None


def source_url_expiry(source_url: str) -> datetime | None:
    """Read the expiry from common AWS, Google Cloud, and Azure signed URLs."""
    query = {
        key.casefold(): values[-1]
        for key, values in parse_qs(urlsplit(source_url).query).items()
        if values
    }
    try:
        issued_at = query.get("x-amz-date") or query.get("x-goog-date")
        lifetime = query.get("x-amz-expires") or query.get("x-goog-expires")
        if issued_at and lifetime:
            issued = datetime.strptime(issued_at, "%Y%m%dT%H%M%SZ").replace(
                tzinfo=timezone.utc
            )
            return issued + timedelta(seconds=int(lifetime))
        expires = query.get("expires")
        if expires and expires.isdigit():
            return datetime.fromtimestamp(int(expires), timezone.utc)
        signed_expiry = query.get("se")
        if signed_expiry:
            parsed = datetime.fromisoformat(signed_expiry.replace("Z", "+00:00"))
            if parsed.tzinfo is None:
                parsed = parsed.replace(tzinfo=timezone.utc)
            return parsed.astimezone(timezone.utc)
    except (OverflowError, TypeError, ValueError):
        return None
    return None


class _PinnedHTTPSConnection(http.client.HTTPSConnection):
    """HTTPS connection pinned to an address that passed the SSRF check."""

    def __init__(self, hostname: str, port: int, address: str):
        super().__init__(hostname, port, timeout=CONNECT_TIMEOUT_SECONDS)
        self._approved_address = address

    def connect(self) -> None:
        sock = socket.create_connection(
            (self._approved_address, self.port), timeout=self.timeout
        )
        try:
            self.sock = self._context.wrap_socket(sock, server_hostname=self.host)
        except Exception:
            sock.close()
            raise


class SourceConnector:
    """Validate every redirect and pin each HTTPS request to a public address."""

    def start(self, source_url: str) -> ConnectionStarted:
        """Open one GET and inspect headers before the API accepts the job."""
        url = source_url
        for redirect_count in range(MAX_REDIRECTS + 1):
            connection, response = self._open_get(url)
            try:
                if _is_redirect(response.status):
                    location = response.getheader("Location")
                    if location is None or redirect_count == MAX_REDIRECTS:
                        raise SourceUnavailable
                    url = urljoin(url, location)
                    continue
                if response.status in TRANSIENT_HTTP_STATUSES:
                    raise TransientSourceFailure(
                        _retry_after_seconds(response.getheader("Retry-After"))
                    )
                if not 200 <= response.status < 300:
                    raise SourceUnavailable
                content_length = _content_length(response.getheader("Content-Length"))
                if content_length is not None and content_length > MAX_MEDIA_SIZE_BYTES:
                    raise MediaSizeExceeded
                return ConnectionStarted(content_length=content_length)
            finally:
                response.close()
                connection.close()
        raise SourceUnavailable

    def download_to_path(
        self,
        source_url: str,
        destination: Path,
        *,
        on_progress=None,
    ) -> int:
        """Stream a source to disk, enforcing the byte cap as data arrives."""
        url = source_url
        for redirect_count in range(MAX_REDIRECTS + 1):
            connection, response = self._open_get(url)
            try:
                if _is_redirect(response.status):
                    location = response.getheader("Location")
                    if location is None or redirect_count == MAX_REDIRECTS:
                        raise SourceUnavailable
                    url = urljoin(url, location)
                    continue
                if response.status in TRANSIENT_HTTP_STATUSES:
                    raise TransientSourceFailure(
                        _retry_after_seconds(response.getheader("Retry-After"))
                    )
                if not 200 <= response.status < 300:
                    raise SourceUnavailable

                content_length = _content_length(response.getheader("Content-Length"))
                if content_length is not None and content_length > MAX_MEDIA_SIZE_BYTES:
                    raise MediaSizeExceeded

                total = 0
                destination.parent.mkdir(parents=True, exist_ok=True)
                with destination.open("wb") as output:
                    while True:
                        chunk = response.read(1024 * 1024)
                        if not chunk:
                            break
                        total += len(chunk)
                        if total > MAX_MEDIA_SIZE_BYTES:
                            raise MediaSizeExceeded
                        output.write(chunk)
                        if on_progress is not None:
                            on_progress()
                return total
            except (InvalidSourceURL, MediaSizeExceeded, SourceUnavailable):
                raise
            except (http.client.HTTPException, OSError, ssl.SSLError, ValueError):
                raise TransientSourceFailure from None
            finally:
                response.close()
                connection.close()
        raise SourceUnavailable

    def _open_get(self, source_url: str):
        parsed, hostname, port = validate_source_url_syntax(source_url)
        expires_at = source_url_expiry(source_url)
        if expires_at is not None and datetime.now(timezone.utc) >= expires_at:
            raise ExpiredSourceURL
        addresses = resolve_public_addresses(hostname, port)
        family, address = addresses[0]
        if family not in {socket.AF_INET, socket.AF_INET6}:
            raise InvalidSourceURL("URL de origem inválida.")

        connection = _PinnedHTTPSConnection(hostname, port, address)
        try:
            connection.connect()
            if connection.sock is not None:
                connection.sock.settimeout(READ_TIMEOUT_SECONDS)
            request_target = parsed.path or "/"
            if parsed.query:
                request_target = f"{request_target}?{parsed.query}"
            connection.request(
                "GET",
                request_target,
                headers={"Accept": "*/*", "Connection": "close"},
            )
            return connection, connection.getresponse()
        except (InvalidSourceURL, MediaSizeExceeded, SourceUnavailable):
            connection.close()
            raise
        except (http.client.HTTPException, OSError, ssl.SSLError, ValueError):
            connection.close()
            raise TransientSourceFailure from None


def _is_redirect(status_code: int) -> bool:
    return status_code in {301, 302, 303, 307, 308}


def _content_length(value: str | None) -> int | None:
    if value is None:
        return None
    try:
        length = int(value)
    except ValueError:
        return None
    return length if length >= 0 else None


def _retry_after_seconds(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        seconds = float(value)
        return max(0.0, seconds) if math.isfinite(seconds) else None
    except ValueError:
        try:
            retry_at = parsedate_to_datetime(value)
        except (TypeError, ValueError, OverflowError):
            return None
        if retry_at.tzinfo is None:
            retry_at = retry_at.replace(tzinfo=timezone.utc)
        return max(0.0, (retry_at - datetime.now(timezone.utc)).total_seconds())
