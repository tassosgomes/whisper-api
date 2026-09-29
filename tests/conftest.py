"""Fixtures for HTTP integration tests against the isolated PostgreSQL service."""

from __future__ import annotations

import os
import socket
import ssl
import subprocess
import threading
from pathlib import Path
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Iterator

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import delete
from sqlalchemy.engine import URL, make_url

from app.access.models import Account, Credential
from app.jobs.models import IdempotencyRecord, TranscriptionJob
from app.main import app


TEST_SOURCE_URL_ENCRYPTION_KEY = "1a" * 32
TEST_SOURCE_HOST = "media.example.test"
TEST_SOURCE_IP = "93.184.216.34"


class FakeTranscriber:
    def __init__(self, model_path: Path, model_name: str, cpu_threads: int):
        self.model_path = model_path
        self.model_name = model_name
        self.cpu_threads = cpu_threads
        self.model_load_seconds = 0.0

    def transcribe(self, source: Path) -> tuple[list, float]:
        return [], 0.01


def test_database_url() -> str:
    configured = os.getenv("TEST_DATABASE_URL")
    if configured:
        url = make_url(configured)
    else:
        url = URL.create(
            "postgresql+psycopg",
            username=os.getenv("TEST_POSTGRES_USER", "whisper_test"),
            password=os.getenv("TEST_POSTGRES_PASSWORD", "local-test-only"),
            host=os.getenv("TEST_DATABASE_HOST", "127.0.0.1"),
            port=int(os.getenv("TEST_POSTGRES_PORT", "55432")),
            database=os.getenv("TEST_POSTGRES_DB", "whisper_test"),
        )

    if not url.database or "test" not in url.database.casefold():
        raise RuntimeError(
            "TEST_DATABASE_URL precisa apontar para um banco isolado com 'test' no nome"
        )
    if url.host not in {"localhost", "127.0.0.1", "::1"}:
        raise RuntimeError(
            "Os testes de integração aceitam somente PostgreSQL local isolado"
        )
    return url.render_as_string(hide_password=False)


@pytest.fixture(scope="session")
def controlled_origin_certificate(
    tmp_path_factory: pytest.TempPathFactory,
) -> tuple[Path, Path]:
    certificate_dir = tmp_path_factory.mktemp("controlled-origin-cert")
    certificate_path = certificate_dir / "source.crt"
    key_path = certificate_dir / "source.key"
    subprocess.run(
        [
            "openssl",
            "req",
            "-x509",
            "-newkey",
            "rsa:2048",
            "-nodes",
            "-days",
            "3650",
            "-keyout",
            str(key_path),
            "-out",
            str(certificate_path),
            "-subj",
            f"/CN={TEST_SOURCE_HOST}",
            "-addext",
            f"subjectAltName=DNS:{TEST_SOURCE_HOST}",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    return certificate_path, key_path


@dataclass
class ControlledOrigin:
    port: int
    request_count: int = 0
    content_length_override: int | None = None
    status_code: int = 200
    redirect_location: str | None = None
    body: bytes = b"controlled test media"
    lock: threading.Lock = field(default_factory=threading.Lock)

    @property
    def source_url(self) -> str:
        return (
            f"https://{TEST_SOURCE_HOST}:{self.port}/audio.mp3"
            "?signature=controlled-test-secret"
        )

    def count_requests(self) -> int:
        with self.lock:
            return self.request_count


@pytest.fixture
def controlled_https_origin(
    monkeypatch: pytest.MonkeyPatch,
    controlled_origin_certificate: tuple[Path, Path],
) -> Iterator[ControlledOrigin]:
    certificate_path, key_path = controlled_origin_certificate
    state = ControlledOrigin(port=0)

    class Handler(BaseHTTPRequestHandler):
        def do_GET(self):
            with state.lock:
                state.request_count += 1
                status_code = state.status_code
                body = state.body
                content_length = state.content_length_override
                redirect_location = state.redirect_location
            self.send_response(status_code)
            if redirect_location is not None:
                self.send_header("Location", redirect_location)
            self.send_header(
                "Content-Length",
                str(len(body) if content_length is None else content_length),
            )
            self.end_headers()
            try:
                self.wfile.write(body)
            except OSError:
                # The API closes the streaming response after inspecting headers.
                pass

        def log_message(self, format: str, *args) -> None:
            return

    server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    state.port = server.server_address[1]
    server_context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    server_context.load_cert_chain(str(certificate_path), str(key_path))
    server.socket = server_context.wrap_socket(server.socket, server_side=True)
    server_thread = threading.Thread(target=server.serve_forever, daemon=True)
    server_thread.start()

    original_getaddrinfo = socket.getaddrinfo
    original_create_connection = socket.create_connection

    def getaddrinfo(host, port, *args, **kwargs):
        if host == TEST_SOURCE_HOST:
            return [
                (
                    socket.AF_INET,
                    socket.SOCK_STREAM,
                    socket.IPPROTO_TCP,
                    "",
                    (TEST_SOURCE_IP, port),
                )
            ]
        return original_getaddrinfo(host, port, *args, **kwargs)

    def create_connection(
        address, timeout=socket._GLOBAL_DEFAULT_TIMEOUT, source_address=None
    ):
        host, port = address[:2]
        if host == TEST_SOURCE_IP:
            host = "127.0.0.1"
        return original_create_connection((host, port), timeout, source_address)

    monkeypatch.setattr(socket, "getaddrinfo", getaddrinfo)
    monkeypatch.setattr(socket, "create_connection", create_connection)
    monkeypatch.setenv("SSL_CERT_FILE", str(certificate_path))
    try:
        yield state
    finally:
        server.shutdown()
        server.server_close()
        server_thread.join(timeout=5)


@pytest.fixture
def client(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    controlled_https_origin: ControlledOrigin,
):
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    monkeypatch.setenv("SOURCE_URL_ENCRYPTION_KEY", TEST_SOURCE_URL_ENCRYPTION_KEY)
    input_dir = tmp_path / "input"
    output_dir = tmp_path / "output"
    input_dir.mkdir()
    (input_dir / "sample.wav").write_bytes(b"test media")
    monkeypatch.setenv("APP_INPUT_DIR", str(input_dir))
    monkeypatch.setenv("APP_OUTPUT_DIR", str(output_dir))
    monkeypatch.setattr("app.main.Transcriber", FakeTranscriber)

    with TestClient(app) as test_client:
        test_client.app.state.test_source_url = controlled_https_origin.source_url
        test_client.app.state.test_source = controlled_https_origin
        yield test_client
        engine = test_client.app.state.database_engine
        with engine.begin() as connection:
            connection.execute(delete(IdempotencyRecord))
            connection.execute(delete(TranscriptionJob))
            connection.execute(delete(Credential))
            connection.execute(delete(Account))
