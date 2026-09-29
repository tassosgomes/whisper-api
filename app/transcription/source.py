"""Safe HTTPS connection setup for customer supplied media URLs."""

from __future__ import annotations

import http.client
import socket
import ssl
from dataclasses import dataclass
from urllib.parse import urljoin

from app.transcription.paths import (
    InvalidSourceURL,
    resolve_public_addresses,
    validate_source_url_syntax,
)


MAX_MEDIA_SIZE_BYTES = 5 * 1024**3
CONNECT_TIMEOUT_SECONDS = 10
READ_TIMEOUT_SECONDS = 60
MAX_REDIRECTS = 5


class MediaSizeExceeded(ValueError):
    """The origin reports a media size above the configured maximum."""


class SourceUnavailable(RuntimeError):
    """The source could not be connected to or did not accept a GET request."""


@dataclass(frozen=True)
class ConnectionStarted:
    content_length: int | None


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
    """Validate a source and start one HTTP GET without reading the media body."""

    def start(self, source_url: str) -> ConnectionStarted:
        url = source_url
        for redirect_count in range(MAX_REDIRECTS + 1):
            parsed, hostname, port = validate_source_url_syntax(url)
            addresses = resolve_public_addresses(hostname, port)
            family, address = addresses[0]
            if family not in {socket.AF_INET, socket.AF_INET6}:
                raise InvalidSourceURL("URL de origem inválida.")

            connection = _PinnedHTTPSConnection(hostname, port, address)
            response: http.client.HTTPResponse | None = None
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
                response = connection.getresponse()
                if response.status in {301, 302, 303, 307, 308}:
                    location = response.getheader("Location")
                    if location is None or redirect_count == MAX_REDIRECTS:
                        raise SourceUnavailable
                    url = urljoin(url, location)
                    continue
                if not 200 <= response.status < 300:
                    raise SourceUnavailable

                content_length = _content_length(response.getheader("Content-Length"))
                if content_length is not None and content_length > MAX_MEDIA_SIZE_BYTES:
                    raise MediaSizeExceeded
                return ConnectionStarted(content_length=content_length)
            except (InvalidSourceURL, MediaSizeExceeded, SourceUnavailable):
                raise
            except (http.client.HTTPException, OSError, ssl.SSLError, ValueError):
                raise SourceUnavailable from None
            finally:
                if response is not None:
                    response.close()
                connection.close()
        raise SourceUnavailable


def _content_length(value: str | None) -> int | None:
    if value is None:
        return None
    try:
        length = int(value)
    except ValueError:
        return None
    return length if length >= 0 else None
