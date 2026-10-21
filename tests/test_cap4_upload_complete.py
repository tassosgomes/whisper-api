"""V-02: direct uploads join the existing durable job and processing pipeline."""

import base64
import hashlib
import os
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path
from threading import Barrier
from uuid import UUID

import httpx
import pytest
import yaml
from botocore.exceptions import ClientError
from jsonschema import Draft202012Validator, FormatChecker
from moto.s3.responses import S3Response
from sqlalchemy import func, select, update
from sqlalchemy.orm import Session

from app.jobs.models import TranscriptionJob, TranscriptionUpload
from app.jobs.security import SourceURLCipher
from app.jobs.uploads import UploadManager
from app.jobs.worker import JobWorker
from app.storage.s3 import S3ObjectStore
from app.transcription.models import Segment
from app.transcription.source import SourceConnector


PATH = "/v1/transcription-uploads"


@pytest.fixture(autouse=True)
def moto_sha256_header(monkeypatch):
    # moto 5.2.3 only stores a checksum when the optional SDK algorithm header
    # is supplied. S3/MinIO also accept the SHA-256 header on its own, exactly
    # as returned by our signed PUT contract. Teach the simulator that case;
    # the API, object-store adapter and persisted checksum remain real.
    original = S3Response._get_checksum

    def get_checksum(response, response_headers):
        if response.headers.get("x-amz-checksum-sha256") and not response.headers.get(
            "x-amz-sdk-checksum-algorithm"
        ):
            checksum = response.headers["x-amz-checksum-sha256"]
            response_headers["x-amz-checksum-sha256"] = checksum
            return "SHA256", checksum
        return original(response, response_headers)

    monkeypatch.setattr(S3Response, "_get_checksum", get_checksum)


@pytest.fixture(scope="module")
def contract():
    return yaml.safe_load(
        (
            Path(__file__).resolve().parents[1]
            / "tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml"
        ).read_text()
    )


def _assert_contract(response, contract, status, code=None):
    assert response.status_code == status, response.text
    definition = contract["paths"]["/transcription-uploads/{uploadId}/complete"][
        "post"
    ]["responses"][str(status)]
    if "$ref" in definition:
        definition = contract["components"]["responses"][
            definition["$ref"].rsplit("/", 1)[1]
        ]
    media_type = response.headers["content-type"].split(";")[0]
    Draft202012Validator(
        {
            **definition["content"][media_type]["schema"],
            "components": contract["components"],
        },
        format_checker=FormatChecker(),
    ).validate(response.json())
    if code:
        assert media_type == "application/problem+json"
        assert response.json()["code"] == code
    return response.json()


def _checksum(media):
    return base64.b64encode(hashlib.sha256(media).digest()).decode()


def _request(client, key, media, *, content_type="audio/wav", **overrides):
    payload = {
        "contentType": content_type,
        "sizeBytes": len(media),
        "checksumSha256": _checksum(media),
        **overrides,
    }
    response = client.post(
        PATH,
        headers={"X-API-Key": key, "Idempotency-Key": "complete-test"},
        json=payload,
    )
    assert response.status_code == 201, response.text
    return response.json()


def _put(upload, media):
    response = httpx.put(
        upload["upload"]["url"],
        headers=upload["upload"]["headers"],
        content=media,
        trust_env=False,
    )
    assert response.status_code == 200, response.text


def _complete(client, key, upload):
    return client.post(upload["completeUrl"], headers={"X-API-Key": key})


def _record(client, upload):
    with Session(client.app.state.database_engine) as session:
        return session.get(TranscriptionUpload, upload["uploadId"])


def _job_count(client):
    with client.app.state.database_engine.connect() as connection:
        return connection.scalar(select(func.count()).select_from(TranscriptionJob))


def _status(client, key, job):
    response = client.get(job["statusUrl"], headers={"X-API-Key": key})
    assert response.status_code == 200, response.text
    return response.json()


class RecordingTranscriber:
    def __init__(self):
        self.calls = 0

    def transcribe(self, path):
        assert path.suffix == ".wav"
        self.calls += 1
        return [Segment(0, 0.5, "Uma reunião.")], 1.0


def _worker(client, transcriber=None):
    return JobWorker(
        client.app.state.database_engine,
        SourceURLCipher.from_hex("1a" * 32),
        SourceConnector(),
        client.app.state.object_store,
        transcriber or RecordingTranscriber(),
        client.app.state.job_metrics,
    )


def test_cap4_upload_complete_real_wav_to_result_and_operator_metrics(
    client, upload_credential, upload_wav, contract
):
    key = upload_credential[1]
    media = upload_wav()
    upload = _request(client, key, media, clientReference="rec_complete")
    _put(upload, media)
    response = _complete(client, key, upload)
    job = _assert_contract(response, contract, 202)
    assert response.headers["location"] == job["statusUrl"]
    assert job["status"] == "queued"
    assert job["clientReference"] == "rec_complete"
    record = _record(client, upload)
    assert record.status == "completed" and record.job_id == job["jobId"]
    with Session(client.app.state.database_engine) as session:
        row = session.get(TranscriptionJob, job["jobId"])
        assert row.media_object_key == record.object_key
        assert row.accepted_at == row.queued_at == row.created_at == row.updated_at
        assert row.source_url_encrypted is row.source_connection_started_at is None
        assert row.download_attempts == 0
    store = client.app.state.object_store
    assert list(store.list_keys("media/")) == []
    assert store.get_bytes(record.object_key) == media

    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    queue = client.get("/v1/queue").json()
    assert queue == {"size": 1, "oldestAcceptedAt": job["createdAt"]}
    operational = client.get(f"/v1/jobs/{job['jobId']}").json()
    assert operational["state"] == "queued"
    assert operational["acceptedAt"] == job["createdAt"]
    assert "uploads/" not in str(operational)
    transcriber = RecordingTranscriber()
    worker = _worker(client, transcriber)
    assert worker.run_download_once() is False
    assert _status(client, key, job)["status"] == "queued"
    assert worker.run_processing_once() is True
    assert _status(client, key, job)["status"] == "completed"
    assert transcriber.calls == 1
    assert client.app.state.test_source.count_requests() == 0
    result = client.get(job["statusUrl"] + "/result", headers={"X-API-Key": key})
    assert result.status_code == 200
    schema = contract["components"]["schemas"]["TranscriptionResult"]
    Draft202012Validator({**schema, "components": contract["components"]}).validate(
        result.json()
    )
    assert result.json() == {
        "schemaVersion": 1,
        "jobId": job["jobId"],
        "language": "pt-BR",
        "durationMs": 1000,
        "segments": [{"startMs": 0, "endMs": 500, "text": "Uma reunião."}],
    }
    assert not list(store.list_keys(record.object_key))
    assert client.get("/v1/queue").json()["size"] == 0
    assert client.get(f"/v1/jobs/{job['jobId']}").json()["state"] == "completed"
    durations = client.app.state.job_metrics.snapshot()["durations"]
    assert durations["acceptedToTerminal"]["count"] == 1
    assert durations["acceptedToTerminal"]["secondsTotal"] >= 0
    assert durations["queueWait"]["count"] == 1
    # Replay remains valid after worker deletion of the original media.
    assert (
        _assert_contract(_complete(client, key, upload), contract, 202)["jobId"]
        == job["jobId"]
    )
    assert _job_count(client) == 1


def test_cap4_upload_complete_missing_then_put_retry(
    client, upload_credential, upload_wav, contract
):
    key = upload_credential[1]
    media = upload_wav()
    upload = _request(client, key, media)
    _assert_contract(
        _complete(client, key, upload), contract, 409, "UPLOAD_NOT_RECEIVED"
    )
    assert _record(client, upload).status == "pending"
    assert _job_count(client) == 0
    _put(upload, media)
    assert (
        _assert_contract(_complete(client, key, upload), contract, 202)["status"]
        == "queued"
    )


@pytest.mark.parametrize(
    "mismatch,code",
    [
        ("size", "UPLOAD_SIZE_MISMATCH"),
        ("checksum", "UPLOAD_CHECKSUM_MISMATCH"),
        ("no-checksum", "UPLOAD_CHECKSUM_MISMATCH"),
        ("format", "UNSUPPORTED_MEDIA_FORMAT"),
        ("empty", "UPLOAD_SIZE_MISMATCH"),
    ],
)
def test_cap4_upload_complete_refuses_deletes_and_replays_after_restart(
    client, upload_credential, upload_wav, contract, mismatch, code
):
    key = upload_credential[1]
    media = upload_wav()
    # A real WAV declared as MP3 must fail the container signature check.
    upload = _request(
        client,
        key,
        media,
        content_type="audio/mpeg" if mismatch == "format" else "audio/wav",
    )
    record = _record(client, upload)
    stored = media + b"extra" if mismatch == "size" else media
    if mismatch == "empty":
        stored = b""
    params = {}
    if mismatch != "no-checksum":
        params["ChecksumSHA256"] = (
            _checksum(b"x" * len(media))
            if mismatch == "checksum"
            else _checksum(stored)
        )
    store = client.app.state.object_store
    store.client.put_object(
        Bucket=store.bucket, Key=record.object_key, Body=stored, **params
    )
    _assert_contract(_complete(client, key, upload), contract, 422, code)
    assert not list(store.list_keys(record.object_key))
    record = _record(client, upload)
    assert record.status == "rejected" and record.failure_code == code
    assert record.job_id is None and _job_count(client) == 0
    client.app.state.uploads = UploadManager(
        client.app.state.database_engine, SourceURLCipher.from_hex("1a" * 32), store
    )
    # A still-valid PUT URL cannot turn a durable rejection into acceptance.
    _put(upload, media)
    _assert_contract(_complete(client, key, upload), contract, 422, code)
    assert not list(store.list_keys(record.object_key))
    response = client.post(
        PATH,
        headers={"X-API-Key": key, "Idempotency-Key": "complete-test"},
        json={
            "contentType": record.content_type,
            "sizeBytes": record.size_bytes,
            "checksumSha256": record.checksum_sha256,
        },
    )
    assert response.status_code == 409 and response.json()["code"] == "UPLOAD_REJECTED"


@pytest.mark.parametrize(
    "scope", ["other-account", "other-credential", "missing", "expired"]
)
def test_cap4_upload_complete_neutral_not_found(
    client, upload_credential, upload_wav, contract, scope
):
    owner, key = upload_credential
    upload = _request(client, key, upload_wav())
    if scope == "other-account":
        _, key = client.app.state.credential_store.provision_account(
            "Other upload owner"
        )
    elif scope == "other-credential":
        _, key = client.app.state.credential_store.issue_for_account(
            UUID(owner["accountId"])
        )
    elif scope == "missing":
        upload["completeUrl"] = f"{PATH}/upl_missing/complete"
    else:
        with client.app.state.database_engine.begin() as connection:
            connection.execute(
                update(TranscriptionUpload).values(
                    expires_at=datetime.now(timezone.utc) - timedelta(seconds=1)
                )
            )
    response = _complete(client, key, upload)
    body = _assert_contract(response, contract, 404, "NOT_FOUND")
    assert body["detail"] == "Envio não encontrado."
    assert _job_count(client) == 0


def test_cap4_upload_complete_requires_authentication(client, contract):
    response = client.post(f"{PATH}/upl_missing/complete")
    _assert_contract(response, contract, 401, "UNAUTHORIZED")


def test_cap4_upload_complete_concurrent_calls_create_one_job(
    client, upload_credential, upload_wav, contract
):
    key = upload_credential[1]
    media = upload_wav()
    upload = _request(client, key, media)
    _put(upload, media)
    barrier = Barrier(2)

    def complete(_):
        barrier.wait(timeout=10)
        return _complete(client, key, upload)

    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(complete, range(2)))
    jobs = [_assert_contract(response, contract, 202) for response in responses]
    assert jobs[0] == jobs[1]
    assert _record(client, upload).job_id == jobs[0]["jobId"]
    assert _job_count(client) == 1


def test_cap4_upload_complete_replay_after_pending_expiry(
    client, upload_credential, upload_wav, contract
):
    key = upload_credential[1]
    media = upload_wav()
    upload = _request(client, key, media)
    _put(upload, media)
    first = _assert_contract(_complete(client, key, upload), contract, 202)
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionUpload).values(
                expires_at=datetime.now(timezone.utc) - timedelta(hours=2)
            )
        )
    assert _assert_contract(_complete(client, key, upload), contract, 202) == first
    assert _job_count(client) == 1


@pytest.mark.parametrize(
    "content_type,header",
    [
        ("audio/mpeg", b"ID3\x04\x00\x00\x00\x00\x00\x00"),
        ("audio/mpeg", b"\xff\xfb\x90\x00"),
        ("audio/mp4", b"\x00\x00\x00\x18ftypM4A "),
        ("video/mp4", b"\x00\x00\x00\x18ftypisom"),
        ("video/webm", b"\x1a\x45\xdf\xa3\x87\x42\x82\x84webm"),
        ("video/x-matroska", b"\x1a\x45\xdf\xa3\x8b\x42\x82\x88matroska"),
    ],
)
def test_cap4_upload_complete_container_magic_not_full_decode(
    client, upload_credential, contract, content_type, header
):
    key = upload_credential[1]
    upload = _request(client, key, header, content_type=content_type)
    _put(upload, header)
    job = _assert_contract(_complete(client, key, upload), contract, 202)
    assert job["status"] == "queued"
    assert _worker(client).run_processing_once() is True
    state = _status(client, key, job)
    assert state["status"] == "failed"
    assert state["failure"]["code"] == "UNSUPPORTED_MEDIA_FORMAT"


def test_cap4_upload_complete_real_duration_over_two_hours_fails_in_worker(
    client, upload_credential, upload_wav, contract
):
    key = upload_credential[1]
    # 800 Hz PCM keeps this real 7201-second WAV small; no duration mock.
    media = upload_wav(7201, sample_rate=800)
    upload = _request(client, key, media, durationMs=1000)
    _put(upload, media)
    job = _assert_contract(_complete(client, key, upload), contract, 202)
    transcriber = RecordingTranscriber()
    assert _worker(client, transcriber).run_processing_once() is True
    state = _status(client, key, job)
    assert state["status"] == "failed"
    assert state["failure"]["code"] == "MEDIA_DURATION_LIMIT_EXCEEDED"
    assert transcriber.calls == 0
    assert not list(
        client.app.state.object_store.list_keys(_record(client, upload).object_key)
    )


def test_cap4_upload_complete_job_and_upload_roll_back_together(
    client, upload_credential, upload_wav, monkeypatch, contract
):
    from app.jobs import uploads

    key = upload_credential[1]
    media = upload_wav()
    upload = _request(client, key, media)
    _put(upload, media)
    create = uploads.create_uploaded_job

    def fail_after_insert(*args):
        create(*args)
        raise RuntimeError("simulated transaction failure")

    with monkeypatch.context() as patch:
        patch.setattr(uploads, "create_uploaded_job", fail_after_insert)
        with pytest.raises(RuntimeError, match="simulated transaction failure"):
            _complete(client, key, upload)
    assert _job_count(client) == 0
    record = _record(client, upload)
    assert record.status == "pending" and record.job_id is None
    _assert_contract(_complete(client, key, upload), contract, 202)
    assert _job_count(client) == 1


def test_cap4_upload_complete_storage_failure_is_not_missing_object(
    client, upload_credential, upload_wav, monkeypatch
):
    key = upload_credential[1]
    upload = _request(client, key, upload_wav())

    def forbidden(**kwargs):
        raise ClientError({"Error": {"Code": "AccessDenied"}}, "HeadObject")

    monkeypatch.setattr(client.app.state.object_store.client, "head_object", forbidden)
    with pytest.raises(ClientError):
        _complete(client, key, upload)
    assert _record(client, upload).status == "pending"
    assert _job_count(client) == 0


def test_cap4_upload_complete_minio_real_storage(
    client, upload_credential, upload_wav, monkeypatch, contract
):
    endpoint = os.getenv("S3_SMOKE_ENDPOINT")
    if not endpoint:
        pytest.skip("S3_SMOKE_ENDPOINT não configurado")
    store = S3ObjectStore(
        bucket=os.getenv("S3_SMOKE_BUCKET", "whisper-temporary"),
        endpoint_url=endpoint,
        access_key_id=os.getenv("S3_SMOKE_ACCESS_KEY_ID", "whisper-local-root"),
        secret_access_key=os.getenv(
            "S3_SMOKE_SECRET_ACCESS_KEY", "local-minio-root-placeholder"
        ),
    )
    key = upload_credential[1]
    media = upload_wav()
    with monkeypatch.context() as patch:
        patch.setattr(client.app.state.uploads, "_object_store", store)
        patch.setattr(client.app.state, "object_store", store)
        upload = _request(client, key, media)
        object_key = _record(client, upload).object_key
        try:
            _put(upload, media)
            stored = store.inspect_upload(object_key)
            assert stored.size_bytes == len(media)
            assert stored.checksum_sha256 == _checksum(media)
            assert stored.first_bytes == media[:4096]
            job = _assert_contract(_complete(client, key, upload), contract, 202)
            assert _worker(client).run_processing_once() is True
            assert _status(client, key, job)["status"] == "completed"
            assert not list(store.list_keys(object_key))
        finally:
            store.delete(object_key)
            record = _record(client, upload)
            if record.job_id:
                store.delete(store.result_key(record.job_id))
