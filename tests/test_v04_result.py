"""V-04 public result access, retention, and object cleanup tests."""

from __future__ import annotations

import io
import time
import wave
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from botocore.exceptions import ClientError
from sqlalchemy import func, select, update

from app.jobs.models import IdempotencyRecord, TranscriptionJob
from app.jobs.retention import RETENTION_SCAN_INTERVAL_SECONDS
from app.jobs.security import SourceURLCipher
from app.jobs.worker import JobWorker
from app.transcription.models import Segment
from app.transcription.source import SourceConnector


class RecordingTranscriber:
    def __init__(self, segments: list[Segment] | None = None) -> None:
        self.segments = segments or []

    def transcribe(self, _media_path):
        return self.segments, 1.0


def _wav_bytes() -> bytes:
    destination = io.BytesIO()
    with wave.open(destination, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        audio.writeframes(b"\x00\x00" * 16000)
    return destination.getvalue()


def _create(client, *, account_name: str = "V-04 account") -> dict:
    source_url = client.app.state.test_source.url_for(f"/{uuid4().hex}.wav")
    _, api_key = client.app.state.credential_store.provision_account(
        f"{account_name} {uuid4().hex}"
    )
    response = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": uuid4().hex},
        json={"sourceUrl": source_url},
    )
    assert response.status_code == 202, response.text
    return {"job": response.json(), "api_key": api_key}


def _worker(client, transcriber=None) -> JobWorker:
    return JobWorker(
        client.app.state.database_engine,
        SourceURLCipher.from_hex("1a" * 32),
        SourceConnector(),
        client.app.state.object_store,
        transcriber or RecordingTranscriber(),
        client.app.state.job_metrics,
        sleeper=lambda _seconds: None,
        jitter=lambda minimum, _maximum: minimum,
    )


def _complete(client, segments: list[Segment] | None = None) -> dict:
    client.app.state.test_source.body = _wav_bytes()
    created = _create(client)
    worker = _worker(client, RecordingTranscriber(segments))
    assert worker.run_download_once() is True
    assert worker.run_processing_once() is True
    return created


def _get_result(client, job_id: str, api_key: str):
    return client.get(
        f"/v1/transcriptions/{job_id}/result",
        headers={"X-API-Key": api_key},
    )


def _job_row(client, job_id: str):
    with client.app.state.database_engine.connect() as connection:
        return (
            connection.execute(
                select(
                    TranscriptionJob.id,
                    TranscriptionJob.status,
                    TranscriptionJob.terminal_at,
                ).where(TranscriptionJob.id == job_id)
            )
            .mappings()
            .one_or_none()
        )


def test_v04_result_returns_valid_v1_json_for_the_owning_credential(client):
    created = _complete(client, [Segment(0.125, 0.5, " Olá")])
    job_id = created["job"]["jobId"]

    result = _get_result(client, job_id, created["api_key"])
    state = client.get(
        created["job"]["statusUrl"], headers={"X-API-Key": created["api_key"]}
    )

    assert result.status_code == 200
    assert result.headers["content-type"].split(";")[0] == "application/json"
    assert result.json() == {
        "schemaVersion": 1,
        "jobId": job_id,
        "language": "pt-BR",
        "durationMs": 1000,
        "segments": [{"startMs": 125, "endMs": 500, "text": " Olá"}],
    }
    assert state.status_code == 200
    assert state.json()["status"] == "completed"
    assert state.json()["terminalAt"].endswith("Z")


def test_v04_result_is_hidden_from_other_accounts_and_sibling_credentials(client):
    created = _complete(client)
    owner = client.app.state.credential_store.authenticate(created["api_key"])
    metadata, sibling_key = client.app.state.credential_store.issue_for_account(
        owner.account_id
    )
    assert UUID(metadata["credentialId"]) != owner.credential_id
    _, other_account_key = client.app.state.credential_store.provision_account(
        "V-04 foreign account"
    )
    job_id = created["job"]["jobId"]

    sibling = _get_result(client, job_id, sibling_key)
    foreign = _get_result(client, job_id, other_account_key)
    missing = _get_result(client, "trn_missing", other_account_key)
    for response in (sibling, foreign, missing):
        assert response.status_code == 404
        assert response.json()["code"] == "NOT_FOUND"
    assert sibling.json() == foreign.json() == missing.json()
    assert _get_result(client, job_id, created["api_key"]).status_code == 200


def test_v04_result_is_unavailable_until_complete_and_after_failure(client):
    client.app.state.test_source.body = _wav_bytes()
    client.app.state.test_source.status_codes = [200, 404]
    failed = _create(client)
    worker = _worker(client)
    assert worker.run_download_once() is True
    failed_state = client.get(
        failed["job"]["statusUrl"], headers={"X-API-Key": failed["api_key"]}
    )
    failed_result = _get_result(client, failed["job"]["jobId"], failed["api_key"])
    pending = _create(client)
    pending_result = _get_result(client, pending["job"]["jobId"], pending["api_key"])

    assert pending_result.status_code == 409
    assert pending_result.json()["code"] == "RESULT_NOT_AVAILABLE"
    assert failed_state.json()["status"] == "failed"
    assert failed_result.status_code == 409
    assert failed_result.json()["code"] == "RESULT_NOT_AVAILABLE"


def test_v04_result_expires_at_the_exact_24_hour_boundary(client, monkeypatch):
    created = _complete(client)
    job_id = created["job"]["jobId"]
    terminal_at = _job_row(client, job_id)["terminal_at"]
    boundary = terminal_at + timedelta(hours=24)
    monkeypatch.setattr(
        "app.jobs.executor._utc_now", lambda: boundary - timedelta(microseconds=1)
    )

    assert _get_result(client, job_id, created["api_key"]).status_code == 200
    before = client.get(
        created["job"]["statusUrl"], headers={"X-API-Key": created["api_key"]}
    )
    assert before.status_code == 200

    monkeypatch.setattr("app.jobs.executor._utc_now", lambda: boundary)
    result = _get_result(client, job_id, created["api_key"])
    state = client.get(
        created["job"]["statusUrl"], headers={"X-API-Key": created["api_key"]}
    )
    assert result.status_code == state.status_code == 404
    assert result.json() == state.json()


def test_v04_result_purge_removes_expired_result_and_metadata_at_boundary(
    client, monkeypatch
):
    created = _complete(client)
    job_id = created["job"]["jobId"]
    object_store = client.app.state.object_store
    result_key = f"results/{job_id}/result-v1.json"
    now = datetime.now(timezone.utc)
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(terminal_at=now - timedelta(hours=24))
        )
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)

    assert _worker(client).run_retention_once() is True

    assert _job_row(client, job_id) is None
    with client.app.state.database_engine.connect() as connection:
        idempotency_count = connection.scalar(
            select(func.count())
            .select_from(IdempotencyRecord)
            .where(IdempotencyRecord.job_id == job_id)
        )
    assert idempotency_count == 0
    assert result_key not in list(object_store.list_keys("results/"))
    assert _get_result(client, job_id, created["api_key"]).status_code == 404


def test_v04_result_retention_schedule_wakes_at_next_expiry(client, monkeypatch):
    """A job expiring right after a sweep is purged by its deadline (fake clock)."""
    created = _complete(client)
    job_id = created["job"]["jobId"]
    object_store = client.app.state.object_store
    result_key = f"results/{job_id}/result-v1.json"
    now = datetime.now(timezone.utc)
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(terminal_at=now - timedelta(hours=24) + timedelta(seconds=30))
        )
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)
    worker = _worker(client)

    assert worker.run_retention_once() is False
    assert _job_row(client, job_id) is not None

    delay = worker.retention_delay_seconds(now)
    assert 0 < delay <= 30
    assert delay < RETENTION_SCAN_INTERVAL_SECONDS

    expiry = now + timedelta(seconds=30)
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: expiry)
    assert worker.run_retention_once() is True
    assert _job_row(client, job_id) is None
    assert result_key not in list(object_store.list_keys("results/"))


def test_v04_result_purge_validates_bucket_policy_and_removes_physical_object(
    client, monkeypatch
):
    """ADR-001: effective bucket policy holds and purge leaves no copies."""
    created = _complete(client)
    job_id = created["job"]["jobId"]
    object_store = client.app.state.object_store

    policy = object_store.ensure_retention_compliant()
    assert policy["versioning"] != "Enabled"
    assert policy["object_lock"] is False

    result_key = f"results/{job_id}/result-v1.json"
    now = datetime.now(timezone.utc)
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(terminal_at=now - timedelta(hours=24))
        )
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)
    assert _worker(client).run_retention_once() is True

    assert result_key not in list(object_store.list_keys("results/"))
    with pytest.raises(ClientError):
        object_store.get_bytes(result_key)
    remaining = object_store.client.list_object_versions(
        Bucket=object_store.bucket, Prefix=result_key
    )
    residual = [
        item
        for field in ("Versions", "DeleteMarkers")
        for item in remaining.get(field, [])
        if item.get("Key") == result_key
    ]
    assert residual == []


def test_v04_result_delete_removes_all_versions_when_versioning_enabled(client):
    """DeleteObject alone would leave versions; purge removes them explicitly."""
    object_store = client.app.state.object_store
    key = f"results/trn_versionprobe_{uuid4().hex}/result-v1.json"
    object_store.client.put_bucket_versioning(
        Bucket=object_store.bucket,
        VersioningConfiguration={"Status": "Enabled"},
    )
    try:
        object_store.client.put_object(
            Bucket=object_store.bucket, Key=key, Body=b"version one"
        )
        object_store.client.put_object(
            Bucket=object_store.bucket, Key=key, Body=b"version two"
        )

        object_store.delete(key)

        remaining = object_store.client.list_object_versions(
            Bucket=object_store.bucket, Prefix=key
        )
        residual = [
            item
            for field in ("Versions", "DeleteMarkers")
            for item in remaining.get(field, [])
            if item.get("Key") == key
        ]
        assert residual == []
        assert key not in list(object_store.list_keys("results/"))
    finally:
        leftover = object_store.client.list_object_versions(
            Bucket=object_store.bucket, Prefix=key
        )
        targets = [
            {"Key": item["Key"], "VersionId": item["VersionId"]}
            for field in ("Versions", "DeleteMarkers")
            for item in leftover.get(field, [])
            if item.get("Key") == key and item.get("VersionId")
        ]
        for index in range(0, len(targets), 1000):
            object_store.client.delete_objects(
                Bucket=object_store.bucket,
                Delete={"Objects": targets[index : index + 1000], "Quiet": True},
            )
        object_store.client.put_bucket_versioning(
            Bucket=object_store.bucket,
            VersioningConfiguration={"Status": "Suspended"},
        )


def test_v04_result_orphan_cleanup_keeps_objects_for_active_jobs(client):
    client.app.state.test_source.body = _wav_bytes()
    active = _create(client)
    active_id = active["job"]["jobId"]
    object_store = client.app.state.object_store
    active_key = f"results/{active_id}/result-v1.json"
    orphan_id = f"trn_orphan_{uuid4().hex}"
    orphan_result = f"results/{orphan_id}/result-v1.json"
    orphan_media = f"media/{orphan_id}/source.wav"
    object_store.client.put_object(
        Bucket=object_store.bucket, Key=active_key, Body=b"active upload window"
    )
    object_store.client.put_object(
        Bucket=object_store.bucket, Key=orphan_result, Body=b"orphan result"
    )
    object_store.client.put_object(
        Bucket=object_store.bucket, Key=orphan_media, Body=b"orphan media"
    )

    assert _worker(client).run_retention_once() is True

    keys = set(object_store.list_keys("results/")) | set(
        object_store.list_keys("media/")
    )
    assert active_key in keys
    assert orphan_result not in keys
    assert orphan_media not in keys


def test_v04_result_purge_failures_are_measured_and_retried(client, monkeypatch):
    created = _complete(client)
    job_id = created["job"]["jobId"]
    object_store = client.app.state.object_store
    result_key = f"results/{job_id}/result-v1.json"
    now = datetime.now(timezone.utc)
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(terminal_at=now - timedelta(hours=24, seconds=1))
        )
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)
    original_delete = object_store.delete
    failed_once = False

    def fail_first_result_delete(key):
        nonlocal failed_once
        if key == result_key and not failed_once:
            failed_once = True
            raise RuntimeError("temporary object store failure")
        original_delete(key)

    monkeypatch.setattr(object_store, "delete", fail_first_result_delete)
    worker = _worker(client)
    worker.run_retention_once()
    assert _job_row(client, job_id) is not None
    assert (
        client.app.state.job_metrics.snapshot()["failures"]["retention.cleanup_failed"]
        == 1
    )

    assert worker.run_retention_once() is True
    assert _job_row(client, job_id) is None
    assert result_key not in list(object_store.list_keys("results/"))


def test_v04_result_composed_api_worker_smoke_follows_public_status_url(
    all_roles_client,
):
    client = all_roles_client
    client.app.state.test_source.body = _wav_bytes()
    _, api_key = client.app.state.credential_store.provision_account(
        f"V-04 composition {uuid4().hex}"
    )
    created = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": uuid4().hex},
        json={"sourceUrl": client.app.state.test_source.url_for(f"/{uuid4().hex}.wav")},
    )
    assert created.status_code == 202
    status_url = created.json()["statusUrl"]
    assert created.headers["location"] == status_url

    deadline = time.monotonic() + 30
    state = client.get(status_url, headers={"X-API-Key": api_key})
    while (
        state.json()["status"] not in {"completed", "failed"}
        and time.monotonic() < deadline
    ):
        time.sleep(0.1)
        state = client.get(status_url, headers={"X-API-Key": api_key})
    assert state.status_code == 200
    assert state.json()["status"] == "completed"
    assert state.json()["terminalAt"].endswith("Z")

    result = client.get(f"{status_url}/result", headers={"X-API-Key": api_key})
    assert result.status_code == 200
    assert result.json()["schemaVersion"] == 1
    assert result.json()["jobId"] == created.json()["jobId"]
    assert result.json()["language"] == "pt-BR"
