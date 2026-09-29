"""V-01 authentication, credential lifecycle, and owner isolation tests."""

from __future__ import annotations

import hashlib
from uuid import UUID

from sqlalchemy import text

from app.access.credentials import INITIAL_PERMISSIONS


def _provision(client, name: str = "Test account") -> tuple[dict, str]:
    return client.app.state.credential_store.provision_account(name)


def _assert_problem(response, *, status_code: int, title: str, code: str) -> dict:
    assert response.status_code == status_code
    assert response.headers["content-type"].split(";")[0].strip() == (
        "application/problem+json"
    )
    body = response.json()
    assert body["type"] == "about:blank"
    assert body["title"] == title
    assert body["status"] == status_code
    assert body["code"] == code
    return body


def _create_job(client, api_key: str) -> str:
    response = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key},
        json={"path": "sample.wav"},
    )
    assert response.status_code == 202
    assert "accountId" not in response.json()
    assert "credentialId" not in response.json()
    return response.json()["id"]


def test_v01_access_provisioning_stores_only_hash_and_metadata(client):
    metadata, api_key = _provision(client)
    assert metadata["permissions"] == list(INITIAL_PERMISSIONS)
    assert metadata["createdAt"]
    assert metadata["lastUsedAt"] is None
    assert api_key not in str(metadata)

    with client.app.state.database_engine.connect() as connection:
        row = connection.execute(
            text("SELECT key_hash FROM credentials WHERE id = :id"),
            {"id": UUID(metadata["credentialId"])},
        ).one()
        columns = connection.execute(
            text(
                "SELECT column_name FROM information_schema.columns "
                "WHERE table_name = 'credentials'"
            )
        ).scalars().all()

    assert row.key_hash == hashlib.sha256(api_key.encode("utf-8")).hexdigest()
    assert api_key not in row.key_hash
    assert "api_key" not in columns


def test_v01_access_active_key_authorizes_http_and_records_last_use(client):
    metadata, api_key = _provision(client)

    job_id = _create_job(client, api_key)
    response = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": api_key},
    )

    assert response.status_code == 200
    assert response.json()["id"] == job_id
    assert client.app.state.credential_store.metadata(UUID(metadata["credentialId"]))[
        "lastUsedAt"
    ] is not None


def test_v01_access_missing_invalid_and_revoked_keys_return_safe_401(client):
    metadata, revoked_key = _provision(client)
    client.app.state.credential_store.revoke(UUID(metadata["credentialId"]))
    invalid_key = "wsk_00000000000000000000000000000000.invalid"

    missing = client.get("/v1/transcriptions/not-a-job")
    invalid = client.get(
        "/v1/transcriptions/not-a-job",
        headers={"X-API-Key": invalid_key},
    )
    revoked = client.get(
        "/v1/transcriptions/not-a-job",
        headers={"X-API-Key": revoked_key},
    )

    assert [missing.status_code, invalid.status_code, revoked.status_code] == [401, 401, 401]
    for response in (missing, invalid, revoked):
        _assert_problem(
            response,
            status_code=401,
            title="Autenticação necessária",
            code="UNAUTHORIZED",
        )
        assert response.headers["www-authenticate"] == "APIKey"
    assert missing.json() == invalid.json() == revoked.json()
    assert revoked_key not in revoked.text
    assert invalid_key not in invalid.text


def test_v01_access_rotation_keeps_credential_and_existing_job_access(client):
    metadata, old_key = _provision(client)
    job_id = _create_job(client, old_key)

    rotated_metadata, new_key = client.app.state.credential_store.rotate(
        UUID(metadata["credentialId"])
    )
    old_response = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": old_key},
    )
    new_response = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": new_key},
    )

    assert rotated_metadata["credentialId"] == metadata["credentialId"]
    assert rotated_metadata["rotatedAt"]
    _assert_problem(
        old_response,
        status_code=401,
        title="Autenticação necessária",
        code="UNAUTHORIZED",
    )
    assert old_key not in old_response.text
    assert new_response.status_code == 200
    assert new_response.json()["id"] == job_id


def test_v01_access_other_account_gets_neutral_not_found(client):
    owner_metadata, owner_key = _provision(client, "Owner account")
    other_metadata, other_key = _provision(client, "Other account")
    job_id = _create_job(client, owner_key)

    foreign_job = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": other_key},
    )
    nonexistent = client.get(
        "/v1/transcriptions/not-a-job",
        headers={"X-API-Key": other_key},
    )

    assert owner_metadata["accountId"] != other_metadata["accountId"]
    assert foreign_job.status_code == nonexistent.status_code == 404
    for response in (foreign_job, nonexistent):
        _assert_problem(
            response,
            status_code=404,
            title="Recurso não encontrado",
            code="NOT_FOUND",
        )
    assert foreign_job.json() == nonexistent.json()


def test_v01_access_other_credential_in_same_account_gets_neutral_not_found(client):
    owner_metadata, owner_key = _provision(client, "Shared account")
    sibling_metadata, sibling_key = client.app.state.credential_store.issue_for_account(
        UUID(owner_metadata["accountId"])
    )
    job_id = _create_job(client, owner_key)

    foreign_job = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": sibling_key},
    )
    own_job = client.get(
        f"/v1/transcriptions/{job_id}",
        headers={"X-API-Key": owner_key},
    )

    assert owner_metadata["accountId"] == sibling_metadata["accountId"]
    assert owner_metadata["credentialId"] != sibling_metadata["credentialId"]
    _assert_problem(
        foreign_job,
        status_code=404,
        title="Recurso não encontrado",
        code="NOT_FOUND",
    )
    assert own_job.status_code == 200
