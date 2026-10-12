"""V-06 allowlist de destinos privados confiáveis (ADR-003)."""

from __future__ import annotations

import socket
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytest

from app.jobs.webhooks import (
    WebhookConfigurationError,
    WebhookHTTPTransport,
    _validate_webhook_url,
)
from app.transcription.paths import (
    InvalidSourceURL,
    TrustedDestinations,
    resolve_destination_addresses,
    validate_source_url_syntax,
)
from app.transcription.source import SourceConnector

PRIVATE_IP = "10.0.0.5"
MINIO_HOST = "minio.internal.test"
HOOK_HOST = "hooks.internal.test"
OTHER_HOST = "other.internal.test"


class _Origin:
    """Servidor HTTP local que registra requisições e responde por caminho."""

    def __init__(self):
        self.requests: list[tuple[str, str]] = []
        self.redirects: dict[str, str] = {}
        origin = self

        class Handler(BaseHTTPRequestHandler):
            def _handle(self):
                length = int(self.headers.get("Content-Length", "0"))
                self.rfile.read(length)
                origin.requests.append((self.command, self.path))
                if self.path in origin.redirects:
                    self.send_response(302)
                    self.send_header("Location", origin.redirects[self.path])
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                body = b"media-bytes"
                self.send_response(200)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                if self.command == "GET":
                    self.wfile.write(body)

            do_GET = _handle
            do_POST = _handle

            def log_message(self, format, *args):
                return

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.port = self.server.server_address[1]
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


@pytest.fixture
def fake_dns(monkeypatch):
    """Resolve nomes de teste para IPs controlados, sem DNS real."""
    table: dict[str, list[str]] = {}
    original = socket.getaddrinfo

    def getaddrinfo(host, port, *args, **kwargs):
        if host == "127.0.0.1":  # usado pelo próprio servidor/cliente de teste
            return original(host, port, *args, **kwargs)
        answers = []
        for address in table.get(host, []):
            family = socket.AF_INET6 if ":" in address else socket.AF_INET
            sockaddr = (address, port, 0, 0) if ":" in address else (address, port)
            answers.append(
                (family, socket.SOCK_STREAM, socket.IPPROTO_TCP, "", sockaddr)
            )
        if not answers:
            raise socket.gaierror("desconhecido")
        return answers

    monkeypatch.setattr(socket, "getaddrinfo", getaddrinfo)
    return table


@pytest.fixture
def origin(monkeypatch):
    """Redireciona qualquer conexão pinada a um IP de teste para o servidor local."""
    server = _Origin()
    dialed: list[str] = []
    original = socket.create_connection

    def create_connection(address, timeout=socket._GLOBAL_DEFAULT_TIMEOUT, **kwargs):
        dialed.append(address[0])
        return original(("127.0.0.1", server.port), timeout)

    monkeypatch.setattr(socket, "create_connection", create_connection)
    server.dialed = dialed
    try:
        yield server
    finally:
        server.close()


# --- política de resolução -------------------------------------------------


def test_v06_empty_allowlist_keeps_rejecting_private_hosts(fake_dns):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    fake_dns["public.test"] = ["93.184.216.34"]
    for trusted in (None, TrustedDestinations(), TrustedDestinations.parse("  , ")):
        with pytest.raises(InvalidSourceURL):
            resolve_destination_addresses(MINIO_HOST, 443, "https", trusted)
    assert resolve_destination_addresses("public.test", 443, "https", None) == [
        (socket.AF_INET, "93.184.216.34")
    ]


def test_v06_listed_private_host_and_port_is_accepted(fake_dns):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    fake_dns[OTHER_HOST] = [PRIVATE_IP]
    trusted = TrustedDestinations.parse(f"{MINIO_HOST}:9443")

    assert resolve_destination_addresses(MINIO_HOST, 9443, "https", trusted) == [
        (socket.AF_INET, PRIVATE_IP)
    ]
    with pytest.raises(InvalidSourceURL):  # outra porta
        resolve_destination_addresses(MINIO_HOST, 443, "https", trusted)
    with pytest.raises(InvalidSourceURL):  # outro host, mesmo IP
        resolve_destination_addresses(OTHER_HOST, 9443, "https", trusted)


def test_v06_host_entry_without_port_means_default_port_of_its_scheme(fake_dns):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    trusted = TrustedDestinations.parse(f"{MINIO_HOST}, http://{MINIO_HOST}")

    resolve_destination_addresses(MINIO_HOST, 443, "https", trusted)
    resolve_destination_addresses(MINIO_HOST, 80, "http", trusted)
    with pytest.raises(InvalidSourceURL):
        resolve_destination_addresses(MINIO_HOST, 8443, "https", trusted)


def test_v06_cidr_requires_every_resolved_address_inside_the_range(fake_dns):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    fake_dns[OTHER_HOST] = [PRIVATE_IP, "192.168.1.9"]
    trusted = TrustedDestinations.parse("10.0.0.0/24")

    resolve_destination_addresses(MINIO_HOST, 443, "https", trusted)
    resolve_destination_addresses(MINIO_HOST, 9443, "https", trusted)  # qualquer porta
    with pytest.raises(InvalidSourceURL):
        resolve_destination_addresses(OTHER_HOST, 443, "https", trusted)


def test_v06_http_requires_explicit_marker(fake_dns):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    url = f"http://{MINIO_HOST}:9000/bucket/obj"

    # sem allowlist e com entrada sem marcador: sintaxe recusa http
    for trusted in (None, TrustedDestinations.parse(f"{MINIO_HOST}:9000")):
        with pytest.raises(InvalidSourceURL):
            validate_source_url_syntax(url, trusted)
        with pytest.raises(InvalidSourceURL):
            resolve_destination_addresses(MINIO_HOST, 9000, "http", trusted)

    marked = TrustedDestinations.parse(f"http://{MINIO_HOST}:9000")
    _, hostname, port = validate_source_url_syntax(url, marked)
    assert (hostname, port) == (MINIO_HOST, 9000)
    resolve_destination_addresses(hostname, port, "http", marked)
    # a entrada http não autoriza https no mesmo destino
    with pytest.raises(InvalidSourceURL):
        resolve_destination_addresses(MINIO_HOST, 9000, "https", marked)
    # nem http em outra porta
    with pytest.raises(InvalidSourceURL):
        validate_source_url_syntax(f"http://{MINIO_HOST}:9001/x", marked)

    cidr = TrustedDestinations.parse("http://10.0.0.0/8")
    validate_source_url_syntax(url, cidr)
    resolve_destination_addresses(MINIO_HOST, 9000, "http", cidr)
    with pytest.raises(InvalidSourceURL):  # CIDR sem marcador não libera http
        resolve_destination_addresses(
            MINIO_HOST, 9000, "http", TrustedDestinations.parse("10.0.0.0/8")
        )


@pytest.mark.parametrize(
    "address",
    [
        "127.0.0.1",
        "::1",
        "169.254.169.254",
        "fe80::1",
        "224.0.0.1",
        "ff02::1",
        "::ffff:127.0.0.1",
        "0.0.0.0",
    ],
)
def test_v06_loopback_link_local_and_multicast_are_never_allowed(fake_dns, address):
    fake_dns[MINIO_HOST] = [address]
    fake_dns[OTHER_HOST] = [PRIVATE_IP, address]
    trusted = TrustedDestinations.parse(
        f"{MINIO_HOST}, {OTHER_HOST}, http://{MINIO_HOST}, "
        "0.0.0.0/0, ::/0, http://0.0.0.0/0, http://::/0"
    )
    for host in (MINIO_HOST, OTHER_HOST):
        for scheme in ("https", "http"):
            with pytest.raises(InvalidSourceURL):
                resolve_destination_addresses(
                    host, 80 if scheme == "http" else 443, scheme, trusted
                )


def test_v06_parse_accepts_documented_syntax_and_rejects_malformed_entries():
    parsed = TrustedDestinations.parse(
        "minio:9000, http://minio:9000, 10.0.0.0/8, http://fd00::/8, "
        "[fd00::5]:8443, Hooks.Internal"
    )
    assert len(parsed.hosts) == 4
    assert len(parsed.networks) == 2
    for invalid in ("*.internal", "host:99999", "host:abc", "10.0.0.1/99", "u@host"):
        with pytest.raises(ValueError):
            TrustedDestinations.parse(invalid)


# --- origem (download) -------------------------------------------------------


def test_v06_source_downloads_over_pinned_http_from_listed_private_host(
    fake_dns, origin, tmp_path
):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    url = f"http://{MINIO_HOST}:9000/bucket/obj?X-Amz-Signature=x"
    connector = SourceConnector(TrustedDestinations.parse(f"http://{MINIO_HOST}:9000"))

    destination = tmp_path / "media.bin"
    assert connector.download_to_path(url, destination) == len(b"media-bytes")
    assert destination.read_bytes() == b"media-bytes"
    assert origin.dialed == [PRIVATE_IP]  # conexão pinada ao IP aprovado
    assert origin.requests == [("GET", "/bucket/obj?X-Amz-Signature=x")]


def test_v06_source_with_empty_allowlist_never_dials_private_host(
    fake_dns, origin, tmp_path
):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    for url in (f"http://{MINIO_HOST}:9000/o", f"https://{MINIO_HOST}:9000/o"):
        with pytest.raises(InvalidSourceURL):
            SourceConnector().download_to_path(url, tmp_path / "x")
    assert origin.dialed == []


def test_v06_redirect_from_listed_host_to_unlisted_private_is_rejected(
    fake_dns, origin, tmp_path
):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    fake_dns[OTHER_HOST] = ["10.0.0.99"]
    origin.redirects["/go"] = f"http://{OTHER_HOST}:9000/secret"
    connector = SourceConnector(TrustedDestinations.parse(f"http://{MINIO_HOST}:9000"))

    with pytest.raises(InvalidSourceURL):
        connector.download_to_path(f"http://{MINIO_HOST}:9000/go", tmp_path / "x")
    assert origin.requests == [("GET", "/go")]  # o segundo salto nunca foi conectado


def test_v06_redirect_to_loopback_or_https_downgrade_is_rejected(
    fake_dns, origin, tmp_path
):
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    fake_dns["loop.test"] = ["127.0.0.1"]
    connector = SourceConnector(
        TrustedDestinations.parse(f"http://{MINIO_HOST}:9000, loop.test, 127.0.0.0/8")
    )
    origin.redirects["/loop"] = "https://loop.test/x"
    with pytest.raises(InvalidSourceURL):
        connector.download_to_path(f"http://{MINIO_HOST}:9000/loop", tmp_path / "x")
    origin.redirects["/meta"] = "http://169.254.169.254/latest/meta-data"
    with pytest.raises(InvalidSourceURL):
        connector.download_to_path(f"http://{MINIO_HOST}:9000/meta", tmp_path / "x")


# --- webhook ---------------------------------------------------------------


def test_v06_webhook_uses_its_own_allowlist_independent_of_sources(
    fake_dns, origin, tmp_path
):
    fake_dns[HOOK_HOST] = [PRIVATE_IP]
    fake_dns[MINIO_HOST] = [PRIVATE_IP]
    sources = TrustedDestinations.parse(f"http://{MINIO_HOST}:9000")
    webhooks = TrustedDestinations.parse(f"http://{HOOK_HOST}:8080")
    hook_url = f"http://{HOOK_HOST}:8080/hook"
    media_url = f"http://{MINIO_HOST}:9000/obj"

    result = WebhookHTTPTransport(webhooks).send(hook_url, b"{}", {"webhook-id": "e1"})
    assert result.status_code == 200
    assert origin.requests == [("POST", "/hook")]
    assert origin.dialed == [PRIVATE_IP]

    # a lista de origens não autoriza o webhook, e vice-versa
    with pytest.raises(WebhookConfigurationError):
        WebhookHTTPTransport(sources).send(hook_url, b"{}", {})
    with pytest.raises(WebhookConfigurationError):
        WebhookHTTPTransport().send(hook_url, b"{}", {})
    with pytest.raises(InvalidSourceURL):
        SourceConnector(webhooks).download_to_path(media_url, tmp_path / "x")
    assert len(origin.requests) == 1


def test_v06_webhook_config_validation_honors_trusted_list(fake_dns):
    fake_dns[HOOK_HOST] = [PRIVATE_IP]
    url = f"http://{HOOK_HOST}:8080/hook"

    with pytest.raises(WebhookConfigurationError):
        _validate_webhook_url(url)
    with pytest.raises(WebhookConfigurationError):
        _validate_webhook_url(f"https://{HOOK_HOST}:8080/hook")
    _, hostname, port = _validate_webhook_url(
        url, TrustedDestinations.parse(f"http://{HOOK_HOST}:8080")
    )
    assert (hostname, port) == (HOOK_HOST, 8080)


def test_v06_webhook_redirects_are_still_not_followed(fake_dns, origin):
    fake_dns[HOOK_HOST] = [PRIVATE_IP]
    origin.redirects["/hook"] = f"http://{HOOK_HOST}:8080/elsewhere"
    transport = WebhookHTTPTransport(
        TrustedDestinations.parse(f"http://{HOOK_HOST}:8080")
    )

    assert transport.send(f"http://{HOOK_HOST}:8080/hook", b"{}", {}).status_code == 302
    assert origin.requests == [("POST", "/hook")]
