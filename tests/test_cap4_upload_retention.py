"""V-03: upload lifecycle, physical deletion and idempotency retention."""

import base64
import hashlib
import logging
from datetime import datetime, timedelta
from uuid import uuid4

import pytest
from sqlalchemy import select, update
from sqlalchemy.orm import Session

from app.jobs.models import TranscriptionJob, TranscriptionUpload
from app.jobs.security import SourceURLCipher
from app.jobs.worker import JobWorker
from app.transcription.source import SourceConnector


PATH = "/v1/transcription-uploads"
DAY = timedelta(hours=24)
IDEMPOTENCY_KEY = "private-retention-idempotency-key"


def _post(client, key, media):
    return client.post(
        PATH,
        headers={"X-API-Key": key, "Idempotency-Key": IDEMPOTENCY_KEY},
        json={
            "contentType": "audio/wav",
            "sizeBytes": len(media),
            "checksumSha256": base64.b64encode(hashlib.sha256(media).digest()).decode(),
            "clientReference": "private-meeting-file.wav",
        },
    )


def _row(client, model, record_id):
    with Session(client.app.state.database_engine) as session:
        return session.get(model, record_id)


def _set(client, model, record_id, **values):
    with client.app.state.database_engine.begin() as connection:
        connection.execute(update(model).where(model.id == record_id).values(**values))


def _put(client, key, media):
    store = client.app.state.object_store
    store.client.put_object(
        Bucket=store.bucket,
        Key=key,
        Body=media,
        ChecksumAlgorithm="SHA256",
        ChecksumSHA256=base64.b64encode(hashlib.sha256(media).digest()).decode(),
    )


def _complete(client, key, upload):
    return client.post(upload["completeUrl"], headers={"X-API-Key": key})


def _clock(monkeypatch, now):
    class Clock(datetime):
        @classmethod
        def now(cls, tz=None):
            return now.astimezone(tz)

    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)
    monkeypatch.setattr("app.jobs.uploads.datetime", Clock)


class Transcriber:
    def __init__(self, fail=False):
        self.fail = fail

    def transcribe(self, path):
        if self.fail:
            raise RuntimeError("controlled inference failure")
        return [], 0.01


def _worker(client, *, fail=False):
    return JobWorker(
        client.app.state.database_engine,
        SourceURLCipher.from_hex("1a" * 32),
        SourceConnector(),
        client.app.state.object_store,
        Transcriber(fail),
        client.app.state.job_metrics,
    )


@pytest.fixture
def upload(client, upload_credential, upload_wav):
    key = upload_credential[1]
    media = upload_wav()
    response = _post(client, key, media)
    assert response.status_code == 201, response.text
    body = response.json()
    record = _row(client, TranscriptionUpload, body["uploadId"])
    _put(client, record.object_key, media)
    return key, media, body, record


def test_cap4_upload_retention_pending_expires_at_boundary(client, upload, monkeypatch):
    key, media, body, record = upload
    worker = _worker(client)
    _clock(monkeypatch, record.expires_at - timedelta(microseconds=1))
    assert worker.run_retention_once() is False
    assert worker.retention_delay_seconds() == pytest.approx(0.000001)
    assert _post(client, key, media).json()["uploadId"] == record.id
    assert client.app.state.object_store.get_bytes(record.object_key) == media

    _clock(monkeypatch, record.expires_at)
    assert worker.run_retention_once() is True
    assert _row(client, TranscriptionUpload, record.id) is None
    assert not list(client.app.state.object_store.list_keys(record.object_key))
    assert _complete(client, key, body).status_code == 404
    fresh = _post(client, key, media)
    assert fresh.status_code == 201
    assert fresh.json()["uploadId"] != record.id
    assert client.app.state.job_metrics.snapshot()["counts"]["orphanObjectsPurged"] == 1


def test_cap4_upload_retention_expired_key_reused_before_scan(
    client, upload, monkeypatch
):
    key, media, body, record = upload
    _clock(monkeypatch, record.expires_at)
    assert _complete(client, key, body).status_code == 404
    fresh = _post(client, key, media)
    assert fresh.status_code == 201
    assert fresh.json()["uploadId"] != record.id
    # Reusing the key discarded the reservation; its old object is now orphaned.
    assert _worker(client).run_retention_once() is True
    assert not list(client.app.state.object_store.list_keys(record.object_key))
    assert _row(client, TranscriptionUpload, fresh.json()["uploadId"]) is not None


@pytest.mark.parametrize("status", ["queued", "processing", "downloading"])
def test_cap4_upload_retention_preserves_active_job(
    client, upload, monkeypatch, status
):
    key, media, body, record = upload
    response = _complete(client, key, body)
    assert response.status_code == 202
    job_id = response.json()["jobId"]
    _set(client, TranscriptionJob, job_id, status=status)
    _clock(monkeypatch, record.expires_at + DAY)
    assert _worker(client).run_retention_once() is False
    assert client.app.state.object_store.get_bytes(record.object_key) == media
    assert _row(client, TranscriptionUpload, record.id).job_id == job_id
    replay = _post(client, key, media)
    assert replay.status_code == 200
    assert replay.json()["jobId"] == job_id


@pytest.mark.parametrize("fail", [False, True], ids=["completed", "failed"])
def test_cap4_upload_retention_terminal_cascade_frees_key(
    client, upload, monkeypatch, fail
):
    key, media, body, record = upload
    response = _complete(client, key, body)
    assert response.status_code == 202
    job_id = response.json()["jobId"]
    worker = _worker(client, fail=fail)
    assert worker.run_processing_once() is True
    job = _row(client, TranscriptionJob, job_id)
    assert job.status == ("failed" if fail else "completed")
    assert not list(client.app.state.object_store.list_keys(record.object_key))
    boundary = job.terminal_at + DAY
    _clock(monkeypatch, boundary - timedelta(microseconds=1))
    assert worker.run_retention_once() is False
    assert _row(client, TranscriptionUpload, record.id) is not None
    replay = _post(client, key, media)
    assert replay.status_code == 200 and replay.json()["jobId"] == job_id
    assert _complete(client, key, body).json()["jobId"] == job_id

    _clock(monkeypatch, boundary)
    assert worker.run_retention_once() is True
    assert _row(client, TranscriptionJob, job_id) is None
    assert _row(client, TranscriptionUpload, record.id) is None
    assert not list(client.app.state.object_store.list_keys("uploads/"))
    assert not list(client.app.state.object_store.list_keys("results/"))
    assert _complete(client, key, body).status_code == 404
    fresh = _post(client, key, media)
    assert fresh.status_code == 201
    assert fresh.json()["uploadId"] != record.id


def test_cap4_upload_retention_rejection_uses_refusal_time(client, upload, monkeypatch):
    key, media, body, record = upload
    _put(client, record.object_key, media + b"wrong-size")
    rejected = _complete(client, key, body)
    assert rejected.status_code == 422
    refusal = _row(client, TranscriptionUpload, record.id).updated_at
    # Refusal can occur near expiry; retention is 24 h from the refusal.
    _set(
        client,
        TranscriptionUpload,
        record.id,
        created_at=refusal - DAY,
        expires_at=refusal,
        updated_at=refusal,
    )
    _put(client, record.object_key, media)  # A late PUT after the refusal.
    _clock(monkeypatch, refusal + DAY - timedelta(microseconds=1))
    worker = _worker(client)
    assert worker.run_retention_once() is True
    assert not list(client.app.state.object_store.list_keys(record.object_key))
    assert _row(client, TranscriptionUpload, record.id).status == "rejected"
    replay = _post(client, key, media)
    assert replay.status_code == 409 and replay.json()["code"] == "UPLOAD_REJECTED"
    assert worker.retention_delay_seconds() == pytest.approx(0.000001)
    _clock(monkeypatch, refusal + DAY)
    assert worker.run_retention_once() is True
    assert _row(client, TranscriptionUpload, record.id) is None
    assert _post(client, key, media).status_code == 201


def test_cap4_upload_retention_terminal_leftover_is_orphan(client, upload):
    key, media, body, record = upload
    response = _complete(client, key, body)
    assert response.status_code == 202
    job_id = response.json()["jobId"]
    worker = _worker(client)
    assert worker.run_processing_once() is True
    _put(client, record.object_key, media)
    assert worker.run_retention_once() is True
    assert not list(client.app.state.object_store.list_keys(record.object_key))
    assert _row(client, TranscriptionJob, job_id) is not None
    assert _post(client, key, media).json()["jobId"] == job_id


def test_cap4_upload_retention_rechecks_job_created_during_scan(
    client, upload, monkeypatch
):
    key, media, body, record = upload
    store = client.app.state.object_store
    listing = store.list_keys
    completed = []

    def complete_during_listing(prefix, **kwargs):
        if prefix == "uploads/":
            response = _complete(client, key, body)
            assert response.status_code == 202
            completed.append(response.json()["jobId"])
        yield from listing(prefix, **kwargs)

    monkeypatch.setattr(store, "list_keys", complete_during_listing)
    assert _worker(client).run_retention_once() is False
    assert len(completed) == 1
    assert store.get_bytes(record.object_key) == media
    assert _row(client, TranscriptionUpload, record.id).job_id == completed[0]


def test_cap4_upload_retention_orphan_versions_and_delete_markers(client):
    store = client.app.state.object_store
    orphan = f"uploads/upl_absent_{uuid4().hex}/private-filename.wav"
    store.client.put_bucket_versioning(
        Bucket=store.bucket, VersioningConfiguration={"Status": "Enabled"}
    )
    try:
        _put(client, orphan, b"one")
        _put(client, orphan, b"two")
        store.client.delete_object(Bucket=store.bucket, Key=orphan)
        assert not list(store.list_keys("uploads/"))
        assert _worker(client).run_retention_once() is True
        remaining = store.client.list_object_versions(
            Bucket=store.bucket, Prefix=orphan
        )
        assert remaining.get("Versions", []) == []
        assert remaining.get("DeleteMarkers", []) == []
        assert (
            client.app.state.job_metrics.snapshot()["counts"]["orphanObjectsPurged"]
            == 1
        )
    finally:
        store.delete(orphan)
        store.client.put_bucket_versioning(
            Bucket=store.bucket, VersioningConfiguration={"Status": "Suspended"}
        )


def test_cap4_upload_retention_retry_metrics_and_no_sensitive_logs(
    client, upload, monkeypatch, caplog
):
    _, _, body, record = upload
    _clock(monkeypatch, record.expires_at)
    store = client.app.state.object_store
    original = store.delete

    def unavailable(key):
        raise RuntimeError(f"{body['upload']['url']} {key} private-meeting-file.wav")

    caplog.set_level(logging.WARNING)
    worker = _worker(client)
    with monkeypatch.context() as patch:
        patch.setattr(store, "delete", unavailable)
        worker.run_retention_once()
    assert list(store.list_keys(record.object_key)) == [record.object_key]
    snapshot = client.app.state.job_metrics.snapshot()
    assert snapshot["failures"]["retention.cleanup_failed"] == 1
    assert snapshot["counts"].get("orphanObjectsPurged", 0) == 0
    assert worker.run_retention_once() is True
    assert store.delete == original
    assert not list(store.list_keys(record.object_key))
    for secret in (
        body["upload"]["url"],
        record.object_key,
        IDEMPOTENCY_KEY,
        "private-meeting-file.wav",
    ):
        assert secret not in caplog.text
        assert secret not in str(client.app.state.job_metrics.snapshot())


def test_cap4_upload_retention_partial_s3_failure_is_retried(client, monkeypatch):
    store = client.app.state.object_store
    orphan = f"uploads/upl_partial_{uuid4().hex}/source.wav"
    _put(client, orphan, b"orphan")
    worker = _worker(client)
    with monkeypatch.context() as patch:
        patch.setattr(
            store.client,
            "delete_objects",
            lambda **kwargs: {"Errors": [{"Key": orphan, "Code": "AccessDenied"}]},
        )
        assert worker.run_retention_once() is False
    assert store.get_bytes(orphan) == b"orphan"
    assert (
        client.app.state.job_metrics.snapshot()["failures"]["retention.cleanup_failed"]
        == 1
    )
    assert worker.run_retention_once() is True
    assert not list(store.list_keys(orphan, include_versions=True))


def test_cap4_upload_retention_empty_expired_reservation_is_removed(
    client, upload, monkeypatch
):
    _, _, _, record = upload
    client.app.state.object_store.delete(record.object_key)
    _clock(monkeypatch, record.expires_at)
    assert _worker(client).run_retention_once() is True
    with client.app.state.database_engine.connect() as connection:
        assert connection.scalar(select(TranscriptionUpload.id)) is None
