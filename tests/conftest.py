"""Fixtures for HTTP integration tests against the isolated PostgreSQL service."""

from __future__ import annotations

import os
import socket
import ssl
import subprocess
import threading
import time
from pathlib import Path
from dataclasses import dataclass, field
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Iterator

import pytest
import boto3
from botocore.config import Config
from fastapi.testclient import TestClient
from moto.server import ThreadedMotoServer
from sqlalchemy import delete
from sqlalchemy.engine import URL, make_url

from app.access.models import Account, Credential
from app.jobs.models import IdempotencyRecord, TranscriptionJob
from app.main import app


TEST_SOURCE_URL_ENCRYPTION_KEY = "1a" * 32
TEST_SOURCE_HOST = "media.example.test"
TEST_SOURCE_IP = "93.184.216.34"
TEST_S3_BUCKET = "whisper-test"


@pytest.fixture(scope="session")
def controlled_s3_endpoint() -> Iterator[str]:
    server = ThreadedMotoServer(ip_address="127.0.0.1", port=0, verbose=False)
    server.start()
    host, port = server.get_host_and_port()
    endpoint = f"http://{host}:{port}"
    setup_client = boto3.client(
        "s3",
        region_name="us-east-1",
        endpoint_url=endpoint,
        aws_access_key_id="test-access-key",
        aws_secret_access_key="test-secret-key",
        config=Config(s3={"addressing_style": "path"}),
    )
    setup_client.create_bucket(Bucket=TEST_S3_BUCKET)
    try:
        yield endpoint
    finally:
        server.stop()


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
    status_codes: list[int] | None = None
    retry_after_values: list[str | None] | None = None
    redirect_location: str | None = None
    body: bytes = b"controlled test media"
    omit_content_length: bool = False
    response_delay_seconds: float = 0.0
    lock: threading.Lock = field(default_factory=threading.Lock)
    active_requests: int = 0
    max_concurrent_requests: int = 0

    @property
    def source_url(self) -> str:
        return (
            f"https://{TEST_SOURCE_HOST}:{self.port}/audio.mp3"
            "?signature=controlled-test-secret"
        )

    def url_for(
        self, path: str, query: str = "signature=controlled-test-secret"
    ) -> str:
        return f"https://{TEST_SOURCE_HOST}:{self.port}{path}?{query}"

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
                request_index = state.request_count
                state.request_count += 1
                state.active_requests += 1
                state.max_concurrent_requests = max(
                    state.max_concurrent_requests, state.active_requests
                )
                status_code = (
                    state.status_codes[request_index]
                    if state.status_codes and request_index < len(state.status_codes)
                    else state.status_code
                )
                body = state.body
                content_length = state.content_length_override
                omit_content_length = state.omit_content_length
                retry_after = (
                    state.retry_after_values[request_index]
                    if state.retry_after_values
                    and request_index < len(state.retry_after_values)
                    else None
                )
                redirect_location = state.redirect_location
            try:
                if state.response_delay_seconds:
                    time.sleep(state.response_delay_seconds)
                self.send_response(status_code)
                if redirect_location is not None:
                    self.send_header("Location", redirect_location)
                if retry_after is not None:
                    self.send_header("Retry-After", retry_after)
                if not omit_content_length:
                    self.send_header(
                        "Content-Length",
                        str(len(body) if content_length is None else content_length),
                    )
                self.end_headers()
                self.wfile.write(body)
            except OSError:
                # The API closes the streaming response after inspecting headers.
                pass
            finally:
                with state.lock:
                    state.active_requests -= 1

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
def all_roles_client(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    controlled_https_origin: ControlledOrigin,
    controlled_s3_endpoint: str,
):
    """Boot the real API + worker role composition in one process."""
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    monkeypatch.setenv("SOURCE_URL_ENCRYPTION_KEY", TEST_SOURCE_URL_ENCRYPTION_KEY)
    monkeypatch.setenv("APP_ROLE", "all")
    monkeypatch.setenv("S3_BUCKET", TEST_S3_BUCKET)
    monkeypatch.setenv("S3_REGION", "us-east-1")
    monkeypatch.setenv("S3_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("S3_ACCESS_KEY_ID", "test-access-key")
    monkeypatch.setenv("S3_SECRET_ACCESS_KEY", "test-secret-key")
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
        store = test_client.app.state.object_store
        for prefix in ("media/", "results/"):
            try:
                response = store.client.list_objects_v2(
                    Bucket=store.bucket, Prefix=prefix
                )
            except Exception:
                continue
            for item in response.get("Contents", []):
                store.delete(item["Key"])


@pytest.fixture
def client(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    controlled_https_origin: ControlledOrigin,
    controlled_s3_endpoint: str,
):
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    monkeypatch.setenv("SOURCE_URL_ENCRYPTION_KEY", TEST_SOURCE_URL_ENCRYPTION_KEY)
    monkeypatch.setenv("APP_ROLE", "api")
    monkeypatch.setenv("S3_BUCKET", TEST_S3_BUCKET)
    monkeypatch.setenv("S3_REGION", "us-east-1")
    monkeypatch.setenv("S3_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("S3_ACCESS_KEY_ID", "test-access-key")
    monkeypatch.setenv("S3_SECRET_ACCESS_KEY", "test-secret-key")
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
        store = test_client.app.state.object_store
        for prefix in ("media/", "results/"):
            try:
                response = store.client.list_objects_v2(
                    Bucket=store.bucket, Prefix=prefix
                )
            except Exception:
                continue
            for item in response.get("Contents", []):
                store.delete(item["Key"])
