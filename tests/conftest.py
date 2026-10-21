"""Fixtures for HTTP integration tests against the isolated PostgreSQL service."""

from __future__ import annotations

import base64
import hashlib
import hmac
import io
import json
import os
import socket
import ssl
import subprocess
import threading
import time
import wave
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

from app.access.models import Account, AdministrativeAudit, Credential
from app.jobs.models import IdempotencyRecord, TranscriptionJob, TranscriptionUpload
from app.main import app


TEST_SOURCE_URL_ENCRYPTION_KEY = "1a" * 32
TEST_WEBHOOK_SECRET_ENCRYPTION_KEY = "2b" * 32
TEST_OPERATOR_SECRET = "operator-test-secret-for-integration-tests"
TEST_SOURCE_HOST = "media.example.test"
TEST_WEBHOOK_HOST = "webhook.example.test"
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
            f"subjectAltName=DNS:{TEST_SOURCE_HOST},DNS:{TEST_WEBHOOK_HOST}",
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
    user_agents: list[str | None] = field(default_factory=list)
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
                state.user_agents.append(self.headers.get("User-Agent"))
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
def operator_cookie_secure_env(monkeypatch, request):
    value = getattr(request, "param", None)
    if value is None:
        monkeypatch.delenv("OPERATOR_COOKIE_SECURE", raising=False)
    else:
        monkeypatch.setenv("OPERATOR_COOKIE_SECURE", value)


@pytest.fixture
def all_roles_client(
    monkeypatch: pytest.MonkeyPatch,
    tmp_path: Path,
    controlled_https_origin: ControlledOrigin,
    controlled_s3_endpoint: str,
    operator_cookie_secure_env,
):
    """Boot the real API + worker role composition in one process."""
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    monkeypatch.setenv("SOURCE_URL_ENCRYPTION_KEY", TEST_SOURCE_URL_ENCRYPTION_KEY)
    monkeypatch.setenv(
        "WEBHOOK_SECRET_ENCRYPTION_KEY", TEST_WEBHOOK_SECRET_ENCRYPTION_KEY
    )
    monkeypatch.setenv("OPERATOR_SECRET", TEST_OPERATOR_SECRET)
    monkeypatch.setenv("APP_ROLE", "all")
    monkeypatch.setenv("S3_BUCKET", TEST_S3_BUCKET)
    monkeypatch.setenv("S3_REGION", "us-east-1")
    monkeypatch.setenv("S3_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("S3_PUBLIC_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("UPLOAD_URL_TTL_SECONDS", "3600")
    monkeypatch.setenv("UPLOAD_PENDING_LIMIT", "5")
    monkeypatch.setenv("S3_ACCESS_KEY_ID", "test-access-key")
    monkeypatch.setenv("S3_SECRET_ACCESS_KEY", "test-secret-key")
    input_dir = tmp_path / "input"
    output_dir = tmp_path / "output"
    input_dir.mkdir()
    (input_dir / "sample.wav").write_bytes(b"test media")
    monkeypatch.setenv("APP_INPUT_DIR", str(input_dir))
    monkeypatch.setenv("APP_OUTPUT_DIR", str(output_dir))
    monkeypatch.setattr("app.main.Transcriber", FakeTranscriber)

    with TestClient(app, base_url="https://testserver") as test_client:
        test_client.app.state.test_source_url = controlled_https_origin.source_url
        test_client.app.state.test_source = controlled_https_origin
        yield test_client
        engine = test_client.app.state.database_engine
        with engine.begin() as connection:
            connection.execute(delete(TranscriptionUpload))
            connection.execute(delete(IdempotencyRecord))
            connection.execute(delete(TranscriptionJob))
            connection.execute(delete(Credential))
            connection.execute(delete(AdministrativeAudit))
            connection.execute(delete(Account))
        store = test_client.app.state.object_store
        for prefix in ("media/", "results/", "uploads/"):
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
    operator_cookie_secure_env,
):
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    monkeypatch.setenv("SOURCE_URL_ENCRYPTION_KEY", TEST_SOURCE_URL_ENCRYPTION_KEY)
    monkeypatch.setenv(
        "WEBHOOK_SECRET_ENCRYPTION_KEY", TEST_WEBHOOK_SECRET_ENCRYPTION_KEY
    )
    monkeypatch.setenv("OPERATOR_SECRET", TEST_OPERATOR_SECRET)
    monkeypatch.setenv("APP_ROLE", "api")
    monkeypatch.setenv("S3_BUCKET", TEST_S3_BUCKET)
    monkeypatch.setenv("S3_REGION", "us-east-1")
    monkeypatch.setenv("S3_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("S3_PUBLIC_ENDPOINT_URL", controlled_s3_endpoint)
    monkeypatch.setenv("UPLOAD_URL_TTL_SECONDS", "3600")
    monkeypatch.setenv("UPLOAD_PENDING_LIMIT", "5")
    monkeypatch.setenv("S3_ACCESS_KEY_ID", "test-access-key")
    monkeypatch.setenv("S3_SECRET_ACCESS_KEY", "test-secret-key")
    input_dir = tmp_path / "input"
    output_dir = tmp_path / "output"
    input_dir.mkdir()
    (input_dir / "sample.wav").write_bytes(b"test media")
    monkeypatch.setenv("APP_INPUT_DIR", str(input_dir))
    monkeypatch.setenv("APP_OUTPUT_DIR", str(output_dir))
    monkeypatch.setattr("app.main.Transcriber", FakeTranscriber)

    with TestClient(app, base_url="https://testserver") as test_client:
        test_client.app.state.test_source_url = controlled_https_origin.source_url
        test_client.app.state.test_source = controlled_https_origin
        yield test_client
        engine = test_client.app.state.database_engine
        with engine.begin() as connection:
            connection.execute(delete(TranscriptionUpload))
            connection.execute(delete(IdempotencyRecord))
            connection.execute(delete(TranscriptionJob))
            connection.execute(delete(Credential))
            connection.execute(delete(AdministrativeAudit))
            connection.execute(delete(Account))
        store = test_client.app.state.object_store
        for prefix in ("media/", "results/", "uploads/"):
            try:
                response = store.client.list_objects_v2(
                    Bucket=store.bucket, Prefix=prefix
                )
            except Exception:
                continue
            for item in response.get("Contents", []):
                store.delete(item["Key"])


@pytest.fixture
def upload_credential(client):
    """A real credential with the app's isolated PostgreSQL and moto bucket."""
    return client.app.state.credential_store.provision_account("CAP-4 uploads")


@pytest.fixture
def upload_wav():
    """Produce real, decodable PCM WAVs, including compact long-duration media."""

    def build(seconds=1, *, sample_rate=8000):
        target = io.BytesIO()
        with wave.open(target, "wb") as audio:
            audio.setnchannels(1)
            audio.setsampwidth(1)
            audio.setframerate(sample_rate)
            audio.writeframes(b"\x80" * (seconds * sample_rate))
        return target.getvalue()

    return build


class ControlledWebhookReceiver:
    def __init__(self, certificate: str, private_key: str):
        self._lock = threading.RLock()
        self._requests: list[dict] = []
        self._accepted_secrets: list[str] = []
        self._statuses: list[int] = [200]
        self._retry_after: list[str | None] = []
        self._redirect_location: str | None = None
        self._seen_ids: set[str] = set()

        receiver = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                content_length = int(self.headers.get("Content-Length", "0"))
                raw_body = self.rfile.read(content_length)
                headers = {key.lower(): value for key, value in self.headers.items()}
                valid, duplicate = receiver._verify(headers, raw_body)
                with receiver._lock:
                    request_index = len(receiver._requests)
                    receiver._requests.append(
                        {
                            "headers": headers,
                            "path": self.path,
                            "body": raw_body,
                            "valid": valid,
                            "duplicate": duplicate,
                        }
                    )
                    status_index = min(request_index, len(receiver._statuses) - 1)
                    status_code = receiver._statuses[status_index]
                    retry_after = (
                        receiver._retry_after[request_index]
                        if request_index < len(receiver._retry_after)
                        else None
                    )
                    location = receiver._redirect_location
                self.send_response(status_code)
                if retry_after is not None:
                    self.send_header("Retry-After", retry_after)
                if location is not None:
                    self.send_header("Location", location)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, format, *args):
                return

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, private_key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        self._server = server
        self._thread = threading.Thread(target=server.serve_forever, daemon=True)
        self._thread.start()
        self.url = f"https://{TEST_WEBHOOK_HOST}:{server.server_address[1]}/terminal"

    def close(self):
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)

    @property
    def requests(self) -> list[dict]:
        with self._lock:
            return list(self._requests)

    def allow_secret(self, secret: str) -> None:
        with self._lock:
            self._accepted_secrets.append(secret)

    def respond_with(
        self,
        *statuses: int,
        retry_after: tuple[str | None, ...] = (),
        redirect_location: str | None = None,
    ) -> None:
        with self._lock:
            self._statuses = list(statuses) or [200]
            self._retry_after = list(retry_after)
            self._redirect_location = redirect_location

    def wait_for_requests(self, count: int, timeout: float = 5.0) -> list[dict]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            requests = self.requests
            if len(requests) >= count:
                return requests
            time.sleep(0.01)
        return self.requests

    def _verify(self, headers: dict[str, str], raw_body: bytes) -> tuple[bool, bool]:
        event_id = headers.get("webhook-id", "")
        timestamp = headers.get("webhook-timestamp", "")
        try:
            payload = json.loads(raw_body)
            event_time = int(timestamp)
        except (ValueError, TypeError, json.JSONDecodeError):
            return False, False
        if payload.get("eventId") != event_id or abs(time.time() - event_time) > 300:
            return False, False
        signatures = headers.get("webhook-signature", "").split()
        signed_content = f"{event_id}.{timestamp}.".encode("ascii") + raw_body
        with self._lock:
            allowed = tuple(self._accepted_secrets)
        valid = False
        for secret in allowed:
            key = base64.b64decode(secret.removeprefix("whsec_"), validate=True)
            expected = "v1," + base64.b64encode(
                hmac.new(key, signed_content, hashlib.sha256).digest()
            ).decode("ascii")
            valid = valid or any(
                hmac.compare_digest(candidate, expected) for candidate in signatures
            )
        with self._lock:
            duplicate = event_id in self._seen_ids
            if valid:
                self._seen_ids.add(event_id)
        return valid, duplicate


@pytest.fixture
def https_webhook_receiver(monkeypatch, controlled_origin_certificate):
    certificate, private_key = controlled_origin_certificate
    receiver = ControlledWebhookReceiver(str(certificate), str(private_key))
    original_getaddrinfo = socket.getaddrinfo
    original_create_connection = socket.create_connection

    def getaddrinfo(host, port, *args, **kwargs):
        if host == TEST_WEBHOOK_HOST:
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
        address,
        timeout=socket._GLOBAL_DEFAULT_TIMEOUT,
        source_address=None,
    ):
        host, port = address[:2]
        if host == TEST_SOURCE_IP:
            host = "127.0.0.1"
        return original_create_connection((host, port), timeout, source_address)

    monkeypatch.setattr(socket, "getaddrinfo", getaddrinfo)
    monkeypatch.setattr(socket, "create_connection", create_connection)
    try:
        yield receiver
    finally:
        receiver.close()
