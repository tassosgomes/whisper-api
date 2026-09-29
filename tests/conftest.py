"""Fixtures for HTTP integration tests against the isolated PostgreSQL service."""

from __future__ import annotations

import os
from pathlib import Path

import pytest
from fastapi.testclient import TestClient
from sqlalchemy import delete
from sqlalchemy.engine import URL, make_url

from app.access.models import Account, Credential
from app.main import app


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
        raise RuntimeError("TEST_DATABASE_URL precisa apontar para um banco isolado com 'test' no nome")
    if url.host not in {"localhost", "127.0.0.1", "::1"}:
        raise RuntimeError("Os testes V-01 aceitam somente PostgreSQL local isolado")
    return url.render_as_string(hide_password=False)


@pytest.fixture
def client(monkeypatch: pytest.MonkeyPatch, tmp_path: Path):
    monkeypatch.setenv("DATABASE_URL", test_database_url())
    input_dir = tmp_path / "input"
    output_dir = tmp_path / "output"
    input_dir.mkdir()
    (input_dir / "sample.wav").write_bytes(b"test media")
    monkeypatch.setenv("APP_INPUT_DIR", str(input_dir))
    monkeypatch.setenv("APP_OUTPUT_DIR", str(output_dir))
    monkeypatch.setattr("app.main.Transcriber", FakeTranscriber)

    with TestClient(app) as test_client:
        yield test_client
        engine = test_client.app.state.database_engine
        with engine.begin() as connection:
            connection.execute(delete(Credential))
            connection.execute(delete(Account))

