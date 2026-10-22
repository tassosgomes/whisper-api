"""V-01 HTTP contract, durable idempotency, and upload admission checks."""

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import parse_qs, urlsplit
from uuid import UUID
import base64
import hashlib
import json
import logging

import httpx
import pytest
import yaml
from jsonschema import Draft202012Validator, FormatChecker
from sqlalchemy import delete, func, select, update
from sqlalchemy.orm import Session

from app.jobs.models import TranscriptionJob, TranscriptionUpload
from app.jobs.uploads import UploadManager
from app.storage.s3 import S3ObjectStore


PATH = "/v1/transcription-uploads"
MEDIA = b"RIFF-test-audio"
CHECKSUM = base64.b64encode(hashlib.sha256(MEDIA).digest()).decode()
PAYLOAD = {
    "contentType": "audio/wav",
    "sizeBytes": len(MEDIA),
    "checksumSha256": CHECKSUM,
    "durationMs": 1000,
    "clientReference": "rec_cap4",
}


@pytest.fixture(scope="module")
def contract():
    return yaml.safe_load(
        (
            Path(__file__).resolve().parents[1]
            / "tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml"
        ).read_text()
    )


def _post(client, api_key, key="cap4-key", payload=None):
    return client.post(
        PATH,
        headers={"X-API-Key": api_key, "Idempotency-Key": key},
        json=PAYLOAD if payload is None else payload,
    )


def _assert_contract(response, contract, status_code, code=None):
    assert response.status_code == status_code, response.text
    definition = contract["paths"]["/transcription-uploads"]["post"]["responses"][
        str(status_code)
    ]
    if "$ref" in definition:
        definition = contract["components"]["responses"][
            definition["$ref"].rsplit("/", 1)[1]
        ]
    media_type = response.headers["content-type"].split(";")[0]
    schema = definition["content"][media_type]["schema"]
    Draft202012Validator(
        {**schema, "components": contract["components"]}, format_checker=FormatChecker()
    ).validate(response.json())
    if code:
        assert response.json()["code"] == code
        assert media_type == "application/problem+json"
    return response.json()


def _count(client, model=TranscriptionUpload):
    with client.app.state.database_engine.connect() as connection:
        return connection.scalar(select(func.count()).select_from(model))


def _set_upload(client, upload_id, **values):
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(TranscriptionUpload)
            .where(TranscriptionUpload.id == upload_id)
            .values(**values)
        )


def test_cap4_upload_request_created_with_signed_headers_and_no_job(
    client, upload_credential, contract
):
    _, api_key = upload_credential
    body = _assert_contract(_post(client, api_key), contract, 201)
    assert body["uploadId"].startswith("upl_")
    assert body["clientReference"] == PAYLOAD["clientReference"]
    assert body["completeUrl"] == f"{PATH}/{body['uploadId']}/complete"
    target = body["upload"]
    assert target["method"] == "PUT"
    assert target["headers"] == {
        "Content-Type": "audio/wav",
        "Content-Length": str(len(MEDIA)),
        "x-amz-checksum-sha256": CHECKSUM,
    }
    query = parse_qs(urlsplit(target["url"]).query)
    assert query["X-Amz-SignedHeaders"] == [
        "content-length;content-type;host;x-amz-checksum-sha256"
    ]
    assert query["X-Amz-Expires"] == ["3600"]
    assert _count(client, TranscriptionJob) == 0
    response = _post(client, api_key, "location")
    assert response.headers["location"] == f"{PATH}/{response.json()['uploadId']}"
    with Session(client.app.state.database_engine) as session:
        record = session.get(TranscriptionUpload, body["uploadId"])
        assert record.object_key == f"uploads/{record.id}/source.wav"
        assert record.job_id is None
        assert record.expires_at - record.created_at == timedelta(hours=24)


@pytest.mark.parametrize(
    "content_type,suffix",
    [
        ("audio/wav", ".wav"),
        ("audio/mpeg", ".mp3"),
        ("audio/mp4", ".m4a"),
        ("video/mp4", ".mp4"),
        ("video/webm", ".webm"),
        ("video/x-matroska", ".mkv"),
    ],
)
def test_cap4_upload_request_supported_formats_and_minimal_payload(
    client, upload_credential, contract, content_type, suffix
):
    payload = {
        "contentType": content_type,
        "sizeBytes": 5368709120,
        "checksumSha256": CHECKSUM,
    }
    body = _assert_contract(
        _post(client, upload_credential[1], payload=payload), contract, 201
    )
    assert "clientReference" not in body
    assert urlsplit(body["upload"]["url"]).path.endswith(f"/source{suffix}")


@pytest.mark.parametrize(
    "changes,status_code,code",
    [
        ({"contentType": "audio/ogg"}, 422, "UNSUPPORTED_MEDIA_FORMAT"),
        ({"sizeBytes": 5368709121}, 413, "MEDIA_SIZE_LIMIT_EXCEEDED"),
        ({"durationMs": 7200001}, 422, "MEDIA_DURATION_LIMIT_EXCEEDED"),
        ({"checksumSha256": "invalid"}, 400, "INVALID_REQUEST"),
        ({"checksumSha256": "a" * 43 + "="}, 400, "INVALID_REQUEST"),
        ({"sizeBytes": 0}, 400, "INVALID_REQUEST"),
        ({"sizeBytes": "12"}, 400, "INVALID_REQUEST"),
        ({"sizeBytes": True}, 400, "INVALID_REQUEST"),
        ({"durationMs": 0}, 400, "INVALID_REQUEST"),
        ({"durationMs": None}, 400, "INVALID_REQUEST"),
        ({"clientReference": None}, 400, "INVALID_REQUEST"),
        ({"clientReference": "x" * 257}, 400, "INVALID_REQUEST"),
        ({"unexpected": True}, 400, "INVALID_REQUEST"),
    ],
)
def test_cap4_upload_request_validation_no_reservation(
    client, upload_credential, contract, changes, status_code, code
):
    _assert_contract(
        _post(client, upload_credential[1], payload={**PAYLOAD, **changes}),
        contract,
        status_code,
        code,
    )
    assert _count(client) == _count(client, TranscriptionJob) == 0


@pytest.mark.parametrize(
    "missing", ["contentType", "sizeBytes", "checksumSha256", "Idempotency-Key"]
)
def test_cap4_upload_request_required_values(
    client, upload_credential, contract, missing
):
    headers = {"X-API-Key": upload_credential[1], "Idempotency-Key": "missing"}
    payload = dict(PAYLOAD)
    if missing in headers:
        del headers[missing]
    else:
        del payload[missing]
    _assert_contract(
        client.post(PATH, headers=headers, json=payload),
        contract,
        400,
        "INVALID_REQUEST",
    )
    assert _count(client) == 0


def test_cap4_upload_request_malformed_json_and_long_key(
    client, upload_credential, contract
):
    headers = {
        "X-API-Key": upload_credential[1],
        "Idempotency-Key": "malformed",
        "Content-Type": "application/json",
    }
    _assert_contract(
        client.post(PATH, headers=headers, content='{"contentType":'),
        contract,
        400,
        "INVALID_REQUEST",
    )
    _assert_contract(
        _post(client, upload_credential[1], "x" * 129), contract, 400, "INVALID_REQUEST"
    )
    assert _count(client) == 0


@pytest.mark.parametrize("key", [None, "invalid-api-key"])
def test_cap4_upload_request_authentication(client, contract, key):
    headers = {"Idempotency-Key": "auth"}
    if key is not None:
        headers["X-API-Key"] = key
    response = client.post(PATH, headers=headers, json=PAYLOAD)
    _assert_contract(response, contract, 401, "UNAUTHORIZED")
    assert response.headers["www-authenticate"] == "APIKey"
    assert _count(client) == 0


@pytest.mark.parametrize(
    "field,value",
    [
        ("contentType", "audio/mpeg"),
        ("sizeBytes", 100),
        ("checksumSha256", base64.b64encode(b"x" * 32).decode()),
        ("durationMs", 2000),
        ("clientReference", "different"),
    ],
)
def test_cap4_upload_request_fingerprint_conflict(
    client, upload_credential, contract, field, value
):
    _assert_contract(_post(client, upload_credential[1]), contract, 201)
    _assert_contract(
        _post(client, upload_credential[1], payload={**PAYLOAD, field: value}),
        contract,
        409,
        "IDEMPOTENCY_KEY_REUSED",
    )
    assert _count(client) == 1


def test_cap4_upload_request_replay_renews_after_hours_and_restart(
    client, upload_credential, contract, monkeypatch
):
    first = _assert_contract(_post(client, upload_credential[1]), contract, 201)
    future = datetime.now(timezone.utc) + timedelta(hours=3)

    class FutureClock(datetime):
        @classmethod
        def now(cls, tz=None):
            return future.astimezone(tz)

    monkeypatch.setattr("app.jobs.uploads.datetime", FutureClock)
    monkeypatch.setattr(
        "botocore.auth.get_current_datetime", lambda: future.replace(tzinfo=None)
    )
    state = client.app.state
    state.uploads = UploadManager(
        state.database_engine, state.source_url_cipher, state.object_store
    )
    replay = _assert_contract(
        _post(
            client, upload_credential[1], payload=dict(reversed(list(PAYLOAD.items())))
        ),
        contract,
        200,
    )
    assert replay["uploadId"] == first["uploadId"]
    assert replay["upload"]["url"] != first["upload"]["url"]
    assert datetime.fromisoformat(replay["expiresAt"].replace("Z", "+00:00")) > future
    assert _count(client) == 1


def test_cap4_upload_request_scope_by_account_and_credential(
    client, upload_credential, contract
):
    metadata, api_key = upload_credential
    _, sibling = client.app.state.credential_store.issue_for_account(
        UUID(metadata["accountId"])
    )
    _, other = client.app.state.credential_store.provision_account(
        "Other CAP-4 account"
    )
    bodies = [
        _assert_contract(_post(client, key), contract, 201)
        for key in (api_key, sibling, other)
    ]
    assert len({body["uploadId"] for body in bodies}) == 3


def test_cap4_upload_request_rejected_replay(client, upload_credential, contract):
    first = _post(client, upload_credential[1]).json()
    _set_upload(
        client,
        first["uploadId"],
        status="rejected",
        failure_code="UPLOAD_CHECKSUM_MISMATCH",
    )
    _assert_contract(
        _post(client, upload_credential[1]), contract, 409, "UPLOAD_REJECTED"
    )


def test_cap4_upload_request_completed_replay_and_job_cascade(
    client, upload_credential, contract
):
    first = _post(client, upload_credential[1]).json()
    engine = client.app.state.database_engine
    with Session(engine) as session, session.begin():
        upload = session.get(TranscriptionUpload, first["uploadId"])
        job = TranscriptionJob(
            id="trn_cap4_completed",
            account_id=upload.account_id,
            credential_id=upload.credential_id,
            status="completed",
            created_at=datetime.now(timezone.utc),
            updated_at=datetime.now(timezone.utc),
        )
        session.add(job)
        session.flush()
        upload.job_id = job.id
        upload.status = "completed"
        # Completed reservations outlive their original pending deadline.
        upload.expires_at = datetime.now(timezone.utc) - timedelta(hours=1)
    body = _assert_contract(_post(client, upload_credential[1]), contract, 200)
    assert body["uploadId"] == first["uploadId"]
    assert body["jobId"] == "trn_cap4_completed"
    assert "upload" not in body and "completeUrl" not in body
    with engine.begin() as connection:
        connection.execute(
            delete(TranscriptionJob).where(TranscriptionJob.id == body["jobId"])
        )
    assert _count(client) == 0


def test_cap4_upload_request_limit_replay_and_expired_pending(
    client, upload_credential, contract
):
    key = upload_credential[1]
    for i in range(5):
        _assert_contract(_post(client, key, f"limit-{i}"), contract, 201)
    response = _post(client, key, "overflow")
    _assert_contract(response, contract, 429, "TOO_MANY_PENDING_UPLOADS")
    assert int(response.headers["retry-after"]) >= 1
    first = _assert_contract(_post(client, key, "limit-0"), contract, 200)
    _set_upload(
        client,
        first["uploadId"],
        expires_at=datetime.now(timezone.utc) - timedelta(seconds=1),
    )
    _assert_contract(_post(client, key, "overflow"), contract, 201)
    assert _count(client) == 6


def test_cap4_upload_request_concurrent_replay_and_admission(
    client, upload_credential, contract
):
    key = upload_credential[1]
    with ThreadPoolExecutor(max_workers=4) as pool:
        responses = list(pool.map(lambda _: _post(client, key), range(4)))
    assert sorted(response.status_code for response in responses) == [
        200,
        200,
        200,
        201,
    ]
    assert len({response.json()["uploadId"] for response in responses}) == 1
    for response in responses:
        _assert_contract(response, contract, response.status_code)
    with ThreadPoolExecutor(max_workers=6) as pool:
        responses = list(pool.map(lambda i: _post(client, key, f"new-{i}"), range(6)))
    assert sorted(response.status_code for response in responses) == [
        201,
        201,
        201,
        201,
        429,
        429,
    ]
    assert _count(client) == 5


def test_cap4_upload_request_public_endpoint_and_configured_limits(
    client, upload_credential, contract, monkeypatch
):
    from app.main import Settings

    monkeypatch.setenv("S3_PUBLIC_ENDPOINT_URL", "https://storage.example.test")
    monkeypatch.setenv("UPLOAD_URL_TTL_SECONDS", "90")
    monkeypatch.setenv("UPLOAD_PENDING_LIMIT", "1")
    settings = Settings.from_env()
    state = client.app.state
    store = S3ObjectStore(
        bucket=settings.S3_BUCKET,
        endpoint_url=settings.S3_ENDPOINT_URL,
        public_endpoint_url=settings.S3_PUBLIC_ENDPOINT_URL,
        access_key_id=settings.S3_ACCESS_KEY_ID,
        secret_access_key=settings.S3_SECRET_ACCESS_KEY,
    )
    state.uploads = UploadManager(
        state.database_engine,
        state.source_url_cipher,
        store,
        url_ttl_seconds=settings.UPLOAD_URL_TTL_SECONDS,
        pending_limit=settings.UPLOAD_PENDING_LIMIT,
    )
    body = _assert_contract(_post(client, upload_credential[1]), contract, 201)
    parsed = urlsplit(body["upload"]["url"])
    assert parsed.scheme == "https" and parsed.netloc == "storage.example.test"
    assert parse_qs(parsed.query)["X-Amz-Expires"] == ["90"]
    _assert_contract(
        _post(client, upload_credential[1], "another"),
        contract,
        429,
        "TOO_MANY_PENDING_UPLOADS",
    )


def test_cap4_upload_request_url_not_persisted_logged_or_measured(
    client, upload_credential, contract, caplog
):
    caplog.set_level(logging.INFO)
    first = _assert_contract(_post(client, upload_credential[1]), contract, 201)
    replay = _assert_contract(_post(client, upload_credential[1]), contract, 200)
    with client.app.state.database_engine.connect() as connection:
        rows = (
            connection.execute(select(TranscriptionUpload.__table__)).mappings().all()
        )
    persisted = json.dumps([dict(row) for row in rows], default=str)
    metrics = client.get("/metrics").text
    for body in (first, replay):
        assert body["upload"]["url"] not in caplog.text
        assert body["upload"]["url"] not in persisted
        assert body["upload"]["url"] not in metrics
    assert "X-Amz-Signature" not in persisted + caplog.text + metrics
    assert "cap4-key" not in persisted


def test_cap4_upload_request_moto_put_accepts_returned_headers(
    client, upload_credential, contract
):
    body = _assert_contract(_post(client, upload_credential[1]), contract, 201)
    target = body["upload"]
    response = httpx.put(
        target["url"], headers=target["headers"], content=MEDIA, trust_env=False
    )
    assert response.status_code == 200
    store = client.app.state.object_store
    assert store.get_bytes(store.upload_key(body["uploadId"], ".wav")) == MEDIA
