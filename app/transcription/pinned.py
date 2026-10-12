"""HTTP(S) connections pinned to an address that passed the SSRF policy."""

from __future__ import annotations

import http.client
import socket


class PinnedHTTPConnection(http.client.HTTPConnection):
    """Plain HTTP connection that only dials the approved address."""

    def __init__(self, hostname: str, port: int, address: str, timeout: float):
        super().__init__(hostname, port, timeout=timeout)
        self._approved_address = address

    def connect(self) -> None:
        self.sock = socket.create_connection(
            (self._approved_address, self.port), timeout=self.timeout
        )


class PinnedHTTPSConnection(http.client.HTTPSConnection):
    """TLS connection that dials the approved address, verifying the hostname."""

    def __init__(self, hostname: str, port: int, address: str, timeout: float):
        super().__init__(hostname, port, timeout=timeout)
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


def open_pinned_connection(
    scheme: str, hostname: str, port: int, address: str, timeout: float
) -> http.client.HTTPConnection:
    connection_class = (
        PinnedHTTPConnection if scheme.casefold() == "http" else PinnedHTTPSConnection
    )
    return connection_class(hostname, port, address, timeout)
