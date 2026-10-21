"""V-03 credential issuance, safe reads, and atomic operator audit."""

from __future__ import annotations

import hashlib
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, func, insert, select
from sqlalchemy.orm import Session

from app.access.credentials import INITIAL_PERMISSIONS
from app.access.models import AdministrativeAudit, Credential


@pytest.fixture
def operator(client):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    return client


def _account(operator, name="Credential owner"):
    response = operator.post("/v1/accounts", json={"name": name})
    assert response.status_code == 201
    return response.json()["accountId"]


def _issue(operator, account_id):
    response = operator.post(f"/v1/accounts/{account_id}/credentials")
    assert response.status_code == 201
    assert response.headers["cache-control"] == "no-store"
    return response


def _counts(client):
    with Session(client.app.state.database_engine) as session:
        return tuple(
            session.scalar(select(func.count()).select_from(model))
            for model in (Credential, AdministrativeAudit)
        )


def _assert_safe(response, *secrets):
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    for value in (*secrets, "apiKey", "key_hash", "keyHash"):
        assert value not in response.text


def test_v03_operator_credential_issue_returns_secret_and_commits_only_hash(operator):
    account_id = _account(operator)
    response = _issue(operator, account_id)
    issued = response.json()
    credential_id = UUID(issued["credentialId"])

    assert response.headers["location"] == f"/v1/credentials/{credential_id}"
    assert issued["accountId"] == account_id
    assert issued["permissions"] == list(INITIAL_PERMISSIONS)
    assert issued["status"] == "active"
    assert issued["createdAt"]
    assert issued["lastUsedAt"] is None
    assert issued["rotatedAt"] is None
    assert issued["revokedAt"] is None
    with Session(operator.app.state.database_engine) as session:
        credential = session.get(Credential, credential_id)
        assert (
            credential.key_hash
            == hashlib.sha256(issued["apiKey"].encode("utf-8")).hexdigest()
        )
        assert issued["apiKey"] not in str(credential.__dict__)
        assert "api_key" not in Credential.__table__.columns.keys()
        audit = session.scalars(
            select(AdministrativeAudit).where(
                AdministrativeAudit.action == "credential_issued"
            )
        ).one()
        assert audit.actor == "operational_credential"
        assert audit.account_id == UUID(account_id)
        assert audit.object_type == "credential"
        assert audit.object_id == str(credential_id)
        assert audit.occurred_at.tzinfo is not None
        assert audit.reason is None
        assert issued["apiKey"] not in str(audit.__dict__)
        assert credential.key_hash not in str(audit.__dict__)
        assert operator.app.state.operator_secret not in str(audit.__dict__)
    assert _counts(operator) == (1, 2)


def test_v03_operator_credential_reads_never_reconstruct_secret(operator):
    account_id = _account(operator)
    response = _issue(operator, account_id)
    issued = response.json()
    expected = {key: value for key, value in issued.items() if key != "apiKey"}

    for _ in range(2):
        detail = operator.get(response.headers["location"])
        listing = operator.get(f"/v1/accounts/{account_id}/credentials")
        _assert_safe(detail, issued["apiKey"])
        _assert_safe(listing, issued["apiKey"])
        assert detail.json() == expected
        assert listing.json() == {
            "data": [expected],
            "pagination": {"page": 1, "size": 10, "total": 1, "totalPages": 1},
        }
    assert operator.get("/operator").headers["cache-control"] == "no-store"
    assert issued["apiKey"] not in operator.get("/operator").text
    assert _counts(operator) == (1, 2)


def test_v03_operator_credential_second_issue_preserves_both_keys_and_ownership(
    operator,
):
    account_id = _account(operator)
    first = _issue(operator, account_id).json()
    second = _issue(operator, account_id).json()
    assert first["credentialId"] != second["credentialId"]
    assert first["apiKey"] != second["apiKey"]

    job_ids = []
    for issued in (first, second):
        response = operator.post(
            "/v1/transcriptions",
            headers={"X-API-Key": issued["apiKey"], "Idempotency-Key": str(uuid4())},
            json={"sourceUrl": operator.app.state.test_source_url},
        )
        assert response.status_code == 202
        job_ids.append(response.json()["jobId"])
        metadata = operator.get(f"/v1/credentials/{issued['credentialId']}")
        _assert_safe(metadata, first["apiKey"], second["apiKey"])
        assert metadata.json()["lastUsedAt"] is not None
        assert metadata.json()["status"] == "active"
    for issued, own_job, other_job in (
        (first, job_ids[0], job_ids[1]),
        (second, job_ids[1], job_ids[0]),
    ):
        headers = {"X-API-Key": issued["apiKey"]}
        assert (
            operator.get(f"/v1/transcriptions/{own_job}", headers=headers).status_code
            == 200
        )
        assert (
            operator.get(f"/v1/transcriptions/{other_job}", headers=headers).status_code
            == 404
        )
    assert _counts(operator) == (2, 3)


def test_v03_operator_credential_audit_failure_rolls_back_issuance(
    operator, monkeypatch
):
    account_id = _account(operator)
    before = _counts(operator)
    attempted_ids = []
    generated_keys = []
    store = operator.app.state.credential_store
    new_key = store._new_key

    def capture_key(credential_id):
        key = new_key(credential_id)
        generated_keys.append(key)
        return key

    def fail_audit(mapper, connection, target):
        if target.action == "credential_issued":
            credential_id = UUID(target.object_id)
            assert (
                connection.scalar(
                    select(Credential.id).where(Credential.id == credential_id)
                )
                == credential_id
            )
            attempted_ids.append(credential_id)
            connection.execute(insert(AdministrativeAudit).values(id=uuid4()))

    monkeypatch.setattr(store, "_new_key", capture_key)
    event.listen(AdministrativeAudit, "before_insert", fail_audit)
    try:
        response = operator.post(f"/v1/accounts/{account_id}/credentials")
    finally:
        event.remove(AdministrativeAudit, "before_insert", fail_audit)

    assert response.status_code == 500
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "INTERNAL_ERROR"
    assert generated_keys[0] not in response.text
    assert "INSERT" not in response.text
    assert _counts(operator) == before
    assert len(attempted_ids) == 1
    assert operator.get(f"/v1/credentials/{attempted_ids[0]}").status_code == 404
    assert store.authenticate(generated_keys[0]) is None
    _issue(operator, account_id)
    assert _counts(operator) == (before[0] + 1, before[1] + 1)


def test_v03_operator_credential_list_is_scoped_paginated_and_stable(operator):
    account_id = _account(operator)
    other_id = _account(operator, "Other account")
    assert operator.get(f"/v1/accounts/{account_id}/credentials").json() == {
        "data": [],
        "pagination": {"page": 1, "size": 10, "total": 0, "totalPages": 0},
    }
    issued = [_issue(operator, account_id).json() for _ in range(12)]
    other = _issue(operator, other_id).json()
    first = operator.get(f"/v1/accounts/{account_id}/credentials")
    second = operator.get(f"/v1/accounts/{account_id}/credentials?_page=2")
    _assert_safe(first, *(item["apiKey"] for item in [*issued, other]))
    _assert_safe(second, *(item["apiKey"] for item in [*issued, other]))
    assert first.json()["pagination"] == {
        "page": 1,
        "size": 10,
        "total": 12,
        "totalPages": 2,
    }
    assert second.json()["pagination"] == {
        "page": 2,
        "size": 10,
        "total": 12,
        "totalPages": 2,
    }
    assert [
        item["credentialId"] for item in first.json()["data"] + second.json()["data"]
    ] == [item["credentialId"] for item in issued]
    assert operator.get(f"/v1/accounts/{account_id}/credentials").json() == first.json()
    assert (
        len(
            operator.get(f"/v1/accounts/{account_id}/credentials?_size=100").json()[
                "data"
            ]
        )
        == 12
    )
    assert (
        operator.get(f"/v1/accounts/{account_id}/credentials?_page=20").json()["data"]
        == []
    )


@pytest.mark.parametrize("query", ["_page=0", "_size=0", "_size=101", "_page=text"])
def test_v03_operator_credential_invalid_pagination_is_rejected(operator, query):
    account_id = _account(operator)
    response = operator.get(f"/v1/accounts/{account_id}/credentials?{query}")
    assert response.status_code == 400
    assert response.json()["code"] == "INVALID_REQUEST"


@pytest.mark.parametrize(
    "method, path",
    [
        ("post", "/v1/accounts/{id}/credentials"),
        ("get", "/v1/accounts/{id}/credentials"),
        ("get", "/v1/credentials/{id}"),
    ],
)
def test_v03_operator_credential_missing_resource_is_safe_not_found(
    operator, method, path
):
    before = _counts(operator)
    response = getattr(operator, method)(path.format(id=uuid4()))
    assert response.status_code == 404
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "NOT_FOUND"
    assert response.json()["detail"] == "O recurso não está disponível."
    assert _counts(operator) == before


@pytest.mark.parametrize(
    "method, path",
    [
        ("post", "/v1/accounts/{account}/credentials"),
        ("get", "/v1/accounts/{account}/credentials"),
        ("get", "/v1/credentials/{credential}"),
    ],
)
def test_v03_operator_credential_customer_key_cannot_access_operations(
    client, method, path
):
    metadata, key = client.app.state.credential_store.provision_account("Customer")
    before = _counts(client)
    response = getattr(client, method)(
        path.format(account=metadata["accountId"], credential=metadata["credentialId"]),
        headers={"X-API-Key": key},
    )
    assert response.status_code == 401
    assert response.json()["code"] == "UNAUTHORIZED"
    assert key not in response.text
    assert _counts(client) == before


def test_v03_operator_credential_metadata_reports_rotation_and_revocation(operator):
    account_id = _account(operator)
    issued = _issue(operator, account_id).json()
    store = operator.app.state.credential_store
    credential_id = UUID(issued["credentialId"])
    _, rotated_key = store.rotate(credential_id)
    rotated = operator.get(f"/v1/credentials/{credential_id}")
    _assert_safe(rotated, issued["apiKey"], rotated_key)
    assert rotated.json()["rotatedAt"] is not None
    assert rotated.json()["status"] == "active"
    store.revoke(credential_id)
    revoked = operator.get(f"/v1/credentials/{credential_id}")
    _assert_safe(revoked, issued["apiKey"], rotated_key)
    assert revoked.json()["status"] == "revoked"
    assert revoked.json()["revokedAt"] is not None
    assert operator.get(f"/v1/accounts/{account_id}/credentials").json()["data"] == [
        revoked.json()
    ]


def test_v03_operator_credential_console_serves_one_time_secret_workflow(operator):
    page = operator.get("/operator")
    assert page.status_code == 200
    for element in (
        "credential-issue",
        "credential-list",
        "credential-detail",
        "credential-secret",
        "credential-copy",
        "credential-dismiss",
    ):
        assert f'id="{element}"' in page.text
    assert "única exibição" in page.text
    assert "perda" in page.text
    assert "rotacionar" in page.text
    assert 'method: "POST"' in page.text
    assert "/v1/accounts/${accountId}/credentials" in page.text
    assert "/v1/credentials/${credential.credentialId}" in page.text
    assert "navigator.clipboard.writeText" in page.text
    assert "clearCredentialSecret" in page.text
    assert "localStorage" not in page.text
    assert "sessionStorage" not in page.text
    operator.delete("/v1/operator-sessions")
    assert 'id="credential-secret"' not in operator.get("/operator").text


def test_v03_operator_credential_openapi_operation_ids(operator):
    paths = operator.get("/openapi.json").json()["paths"]
    assert (
        paths["/v1/accounts/{accountId}/credentials"]["post"]["operationId"]
        == "issueCredential"
    )
    assert (
        paths["/v1/accounts/{accountId}/credentials"]["get"]["operationId"]
        == "listCredentials"
    )
    assert (
        paths["/v1/credentials/{credentialId}"]["get"]["operationId"] == "getCredential"
    )
