"""V-02 durable creation, source connection, and idempotency tests."""

from __future__ import annotations

from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from uuid import UUID

from sqlalchemy import func, select, update

from app.jobs.executor import JobManager
from app.jobs.models import IdempotencyRecord, TranscriptionJob
from app.jobs.security import SourceURLCipher
from app.monitoring.metrics import DownloadStartMetrics
from app.transcription.source import MAX_MEDIA_SIZE_BYTES, SourceConnector


def _provision(client, account_name: str = "V-02 account") -> tuple[dict, str]:
    return client.app.state.credential_store.provision_account(account_name)


def _post(client, api_key: str, idempotency_key: str, payload: dict):
    return client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": idempotency_key},
        json=payload,
    )


def _assert_problem(response, status_code: int, code: str) -> dict:
    assert response.status_code == status_code
    assert response.headers["content-type"].split(";")[0] == "application/problem+json"
    body = response.json()
    assert body["type"] == "about:blank"
    assert body["status"] == status_code
    assert body["code"] == code
    return body


def test_v02_creation_returns_202_after_source_connection_and_public_location(client):
    _, api_key = _provision(client)
    source_url = client.app.state.test_source_url

    response = _post(
        client,
        api_key,
        "create-basic",
        {"sourceUrl": source_url, "clientReference": "asset-7891"},
    )

    assert response.status_code == 202
    body = response.json()
    assert body["jobId"].startswith("trn_")
    assert body["status"] == "downloading"
    assert body["createdAt"].endswith("Z")
    assert body["statusUrl"] == f"/v1/transcriptions/{body['jobId']}"
    assert response.headers["location"] == body["statusUrl"]
    assert body["clientReference"] == "asset-7891"
    assert client.app.state.test_source.count_requests() == 1
    assert source_url not in response.text

    status_response = client.get(body["statusUrl"], headers={"X-API-Key": api_key})
    assert status_response.status_code == 200
    assert status_response.json()["status"] == "downloading"
    assert status_response.json()["jobId"] == body["jobId"]


def test_v02_creation_semantic_retry_reuses_job_and_scopes_by_credential(client):
    metadata, api_key = _provision(client)
    source_url = client.app.state.test_source_url
    payload = {"sourceUrl": source_url, "clientReference": "asset-2"}
    first = _post(client, api_key, "same-key", payload)
    semantic_retry = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": "same-key"},
        content=(f'{{"clientReference":"asset-2","sourceUrl":"{source_url}"}}'),
    )

    assert first.status_code == semantic_retry.status_code == 202
    assert first.json()["jobId"] == semantic_retry.json()["jobId"]
    assert client.app.state.test_source.count_requests() == 1

    _, sibling_key = client.app.state.credential_store.issue_for_account(
        UUID(metadata["accountId"])
    )
    sibling = _post(client, sibling_key, "same-key", payload)
    assert sibling.status_code == 202
    assert sibling.json()["jobId"] != first.json()["jobId"]
    assert client.app.state.test_source.count_requests() == 2


def test_v02_creation_conflicting_payload_returns_409_without_replacing_job(client):
    _, api_key = _provision(client)
    source_url = client.app.state.test_source_url
    original = _post(
        client,
        api_key,
        "conflict-key",
        {"sourceUrl": source_url, "clientReference": "first"},
    )
    conflict = _post(
        client,
        api_key,
        "conflict-key",
        {"sourceUrl": source_url, "clientReference": "different"},
    )

    body = _assert_problem(conflict, 409, "IDEMPOTENCY_KEY_REUSED")
    assert source_url not in conflict.text
    assert client.app.state.test_source.count_requests() == 1
    status_response = client.get(
        original.json()["statusUrl"], headers={"X-API-Key": api_key}
    )
    assert status_response.json()["clientReference"] == "first"
    assert body["instance"] == "/v1/transcriptions"


def test_v02_creation_concurrent_retry_and_restart_preserve_one_durable_job(client):
    metadata, api_key = _provision(client)
    payload = {"sourceUrl": client.app.state.test_source_url}

    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(
            pool.map(
                lambda _: _post(client, api_key, "concurrent-key", payload),
                range(2),
            )
        )

    assert [response.status_code for response in responses] == [202, 202]
    job_id = responses[0].json()["jobId"]
    assert responses[1].json()["jobId"] == job_id
    assert client.app.state.test_source.count_requests() == 1

    encrypted_row = client.app.state.database_engine.connect()
    try:
        ciphertext = encrypted_row.execute(
            select(TranscriptionJob.source_url_encrypted).where(
                TranscriptionJob.id == job_id
            )
        ).scalar_one()
    finally:
        encrypted_row.close()
    assert client.app.state.test_source_url.encode() not in ciphertext

    restarted_manager = JobManager(
        client.app.state.database_engine,
        SourceURLCipher.from_hex("1a" * 32),
        SourceConnector(),
        DownloadStartMetrics(),
    )
    recovered = restarted_manager.get(
        job_id,
        account_id=UUID(metadata["accountId"]),
        credential_id=UUID(metadata["credentialId"]),
    )
    assert recovered["status"] == "downloading"
    assert restarted_manager.source_url_for_recovery(job_id) == payload["sourceUrl"]


def test_v02_creation_allows_same_key_after_120_second_window(client):
    _, api_key = _provision(client)
    source_url = client.app.state.test_source_url
    first = _post(client, api_key, "expiring-key", {"sourceUrl": source_url})
    with client.app.state.database_engine.begin() as connection:
        connection.execute(
            update(IdempotencyRecord)
            .where(IdempotencyRecord.job_id == first.json()["jobId"])
            .values(expires_at=datetime.now(timezone.utc) - timedelta(seconds=1))
        )

    second = _post(
        client,
        api_key,
        "expiring-key",
        {"sourceUrl": source_url, "clientReference": "new-window"},
    )
    assert second.status_code == 202
    assert second.json()["jobId"] != first.json()["jobId"]
    assert client.app.state.test_source.count_requests() == 2


def test_v02_creation_rejects_known_oversize_without_persisting_job(client):
    _, api_key = _provision(client)
    client.app.state.test_source.content_length_override = MAX_MEDIA_SIZE_BYTES + 1

    response = _post(
        client,
        api_key,
        "oversize-key",
        {"sourceUrl": client.app.state.test_source_url},
    )

    _assert_problem(response, 413, "MEDIA_SIZE_LIMIT_EXCEEDED")
    assert client.app.state.test_source.count_requests() == 1
    with client.app.state.database_engine.connect() as connection:
        job_count = connection.scalar(
            select(func.count()).select_from(TranscriptionJob)
        )
    assert job_count == 0


def test_v02_creation_rejects_private_source_and_private_redirect(client):
    _, api_key = _provision(client)
    origin = client.app.state.test_source
    private_url = f"https://127.0.0.1:{origin.port}/private.mp3?token=secret"
    direct = _post(client, api_key, "private-direct", {"sourceUrl": private_url})
    direct_body = _assert_problem(direct, 422, "INVALID_SOURCE_URL")
    assert private_url not in direct.text
    assert "secret" not in direct.text
    assert direct_body["instance"] == "/v1/transcriptions"

    origin.status_code = 302
    origin.redirect_location = private_url
    redirected = _post(
        client,
        api_key,
        "private-redirect",
        {"sourceUrl": origin.source_url},
    )
    _assert_problem(redirected, 422, "INVALID_SOURCE_URL")
    assert origin.count_requests() == 1
    with client.app.state.database_engine.connect() as connection:
        job_count = connection.scalar(
            select(func.count()).select_from(TranscriptionJob)
        )
    assert job_count == 0
