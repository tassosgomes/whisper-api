"""V-04 operator credential lifecycle and transactional audit integration."""

from __future__ import annotations

import hashlib
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, func, insert, select
from sqlalchemy.orm import Session

from app.access.credentials import CredentialRevoked, ReasonRequired
from app.access.models import Account, AdministrativeAudit, Credential
from app.jobs.models import WebhookSecret
from app.jobs.webhooks import WebhookConfigStore


@pytest.fixture
def operator(client):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    return client


def _issue(operator):
    account = operator.post("/v1/accounts", json={"name": "Lifecycle owner"}).json()
    response = operator.post(f"/v1/accounts/{account['accountId']}/credentials")
    assert response.status_code == 201
    return response.json()


def _job(operator, key):
    response = operator.post(
        "/v1/transcriptions",
        headers={"X-API-Key": key, "Idempotency-Key": str(uuid4())},
        json={"sourceUrl": operator.app.state.test_source_url},
    )
    assert response.status_code == 202
    return response.json()["jobId"]


def _snapshot(operator, credential_id):
    with Session(operator.app.state.database_engine) as session:
        credential = session.get(Credential, UUID(credential_id))
        return {
            column.name: getattr(credential, column.name)
            for column in Credential.__table__.columns
        }


def _audit_count(operator):
    with Session(operator.app.state.database_engine) as session:
        return session.scalar(select(func.count()).select_from(AdministrativeAudit))


def _audit(operator, action, issued, *secrets):
    with Session(operator.app.state.database_engine) as session:
        audit = session.scalars(
            select(AdministrativeAudit).where(AdministrativeAudit.action == action)
        ).one()
        assert audit.actor == "operational_credential"
        assert audit.account_id == UUID(issued["accountId"])
        assert audit.object_type == "credential"
        assert audit.object_id == issued["credentialId"]
        assert audit.occurred_at.tzinfo is not None
        for secret in (*secrets, operator.app.state.operator_secret):
            assert secret not in str(audit.__dict__)
        return audit.reason


def test_v04_operator_credential_lifecycle_rotation_keeps_jobs_and_webhook(operator):
    issued = _issue(operator)
    key = issued["apiKey"]
    credential_id = issued["credentialId"]
    account_id = UUID(issued["accountId"])
    webhook = WebhookConfigStore(
        operator.app.state.database_engine, operator.app.state.webhook_secret_cipher
    )
    material = webhook.configure(account_id, "https://media.example.test/events")
    # Include an overlapping previous webhook key in the invariance check.
    webhook.rotate(account_id)

    def webhook_snapshot():
        with Session(operator.app.state.database_engine) as session:
            account = session.get(Account, account_id)
            rows = session.execute(
                select(WebhookSecret.__table__)
                .where(WebhookSecret.account_id == account_id)
                .order_by(WebhookSecret.id)
            ).all()
            return account.webhook_url, account.webhook_disabled_at, rows

    before_webhook = webhook_snapshot()
    job_id = _job(operator, key)
    response = operator.post(f"/v1/credentials/{credential_id}/rotate")
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    rotated = response.json()
    new_key = rotated["apiKey"]
    assert new_key != key
    assert rotated["credentialId"] == credential_id
    assert rotated["accountId"] == issued["accountId"]
    assert rotated["permissions"] == issued["permissions"]
    assert rotated["createdAt"] == issued["createdAt"]
    assert rotated["status"] == "active"
    assert rotated["rotatedAt"]
    assert rotated["revokedAt"] is None
    assert webhook_snapshot() == before_webhook
    assert material not in response.text
    assert (
        operator.get(
            f"/v1/transcriptions/{job_id}", headers={"X-API-Key": key}
        ).status_code
        == 401
    )
    assert (
        operator.get(
            f"/v1/transcriptions/{job_id}", headers={"X-API-Key": new_key}
        ).status_code
        == 200
    )
    assert (
        _snapshot(operator, credential_id)["key_hash"]
        == hashlib.sha256(new_key.encode()).hexdigest()
    )
    for path in (
        f"/v1/credentials/{credential_id}",
        f"/v1/accounts/{issued['accountId']}/credentials",
    ):
        read = operator.get(path)
        assert read.status_code == 200
        for secret in (key, new_key, "apiKey", "key_hash", "keyHash"):
            assert secret not in read.text
    assert (
        _audit(
            operator,
            "credential_rotated",
            issued,
            key,
            new_key,
            hashlib.sha256(new_key.encode()).hexdigest(),
        )
        is None
    )


@pytest.mark.parametrize(
    "body", [None, {}, {"reason": ""}, {"reason": " \n\t "}, {"reason": None}]
)
def test_v04_operator_credential_lifecycle_reason_required_before_store(
    operator, monkeypatch, body
):
    issued = _issue(operator)
    before = _snapshot(operator, issued["credentialId"])
    count = _audit_count(operator)

    def unexpected_revoke(*args, **kwargs):
        pytest.fail("Revocation must not be called without a reason")

    monkeypatch.setattr(
        operator.app.state.credential_store, "revoke", unexpected_revoke
    )
    response = operator.post(
        f"/v1/credentials/{issued['credentialId']}/revoke", json=body
    )
    assert response.status_code == 422
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "REASON_REQUIRED"
    assert issued["apiKey"] not in response.text
    assert _snapshot(operator, issued["credentialId"]) == before
    assert _audit_count(operator) == count
    assert operator.app.state.credential_store.authenticate(issued["apiKey"])


def test_v04_operator_credential_lifecycle_revoke_is_immediate_safe_and_audited(
    operator,
):
    issued = _issue(operator)
    job_id = _job(operator, issued["apiKey"])
    reason = "Chave copiada para o canal errado"
    response = operator.post(
        f"/v1/credentials/{issued['credentialId']}/revoke",
        json={"reason": f"  {reason}  "},
    )
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert response.json()["status"] == "revoked"
    assert response.json()["revokedAt"]
    assert "apiKey" not in response.text
    assert issued["apiKey"] not in response.text
    assert (
        operator.get(f"/v1/credentials/{issued['credentialId']}").json()
        == response.json()
    )
    assert _audit(operator, "credential_revoked", issued, issued["apiKey"]) == reason
    for path in (
        f"/v1/transcriptions/{job_id}",
        f"/v1/transcriptions/{job_id}/result",
        "/v1/transcriptions/unknown-job",
    ):
        refused = operator.get(path, headers={"X-API-Key": issued["apiKey"]})
        anonymous = operator.get(path)
        assert refused.status_code == 401
        assert refused.json() == anonymous.json()
        for value in ("jobId", issued["apiKey"], "queued", "sourceUrl"):
            assert value not in refused.text
    assert (
        operator.post(
            "/v1/transcriptions",
            headers={"X-API-Key": issued["apiKey"], "Idempotency-Key": str(uuid4())},
            json={"sourceUrl": operator.app.state.test_source_url},
        ).status_code
        == 401
    )


def test_v04_operator_credential_lifecycle_revoked_rotation_has_no_effect(operator):
    issued = _issue(operator)
    credential_id = issued["credentialId"]
    assert (
        operator.post(
            f"/v1/credentials/{credential_id}/revoke", json={"reason": "Comprometida"}
        ).status_code
        == 200
    )
    before = _snapshot(operator, credential_id)
    count = _audit_count(operator)
    response = operator.post(f"/v1/credentials/{credential_id}/rotate")
    assert response.status_code == 409
    assert response.json()["code"] == "CREDENTIAL_REVOKED"
    assert "apiKey" not in response.text
    assert _snapshot(operator, credential_id) == before
    assert _audit_count(operator) == count
    with pytest.raises(CredentialRevoked):
        operator.app.state.credential_store.rotate(UUID(credential_id))
    assert operator.app.state.credential_store.authenticate(issued["apiKey"]) is None


@pytest.mark.parametrize("action", ["rotate", "revoke"])
def test_v04_operator_credential_lifecycle_unknown_credential(operator, action):
    before = _audit_count(operator)
    response = operator.post(
        f"/v1/credentials/{uuid4()}/{action}", json={"reason": "Não encontrada"}
    )
    assert response.status_code == 404
    assert response.json()["code"] == "NOT_FOUND"
    assert _audit_count(operator) == before


@pytest.mark.parametrize("action", ["rotate", "revoke"])
def test_v04_operator_credential_lifecycle_customer_cannot_mutate(client, action):
    issued, key = client.app.state.credential_store.provision_account("Customer")
    before = _snapshot(client, issued["credentialId"])
    response = client.post(
        f"/v1/credentials/{issued['credentialId']}/{action}",
        json={"reason": "Customer request"},
        headers={"X-API-Key": key},
    )
    assert response.status_code == 401
    assert response.json()["code"] == "UNAUTHORIZED"
    assert _snapshot(client, issued["credentialId"]) == before


@pytest.mark.parametrize("action", ["rotate", "revoke"])
def test_v04_operator_credential_lifecycle_audit_failure_rolls_back(
    operator, monkeypatch, action
):
    issued = _issue(operator)
    credential_id = issued["credentialId"]
    before = _snapshot(operator, credential_id)
    count = _audit_count(operator)
    store = operator.app.state.credential_store
    generated = []
    new_key = store._new_key
    observed = []

    def capture_key(identifier):
        key = new_key(identifier)
        generated.append(key)
        return key

    def fail_audit(mapper, connection, target):
        if (
            target.action
            == f"credential_{'rotated' if action == 'rotate' else 'revoked'}"
        ):
            row = connection.execute(
                select(Credential.key_hash, Credential.revoked_at).where(
                    Credential.id == UUID(credential_id)
                )
            ).one()
            if action == "rotate":
                assert row.key_hash != before["key_hash"]
            else:
                assert row.revoked_at is not None
            observed.append(target.object_id)
            connection.execute(insert(AdministrativeAudit).values(id=uuid4()))

    monkeypatch.setattr(store, "_new_key", capture_key)
    event.listen(AdministrativeAudit, "before_insert", fail_audit)
    try:
        response = operator.post(
            f"/v1/credentials/{credential_id}/{action}",
            json={"reason": "Rollback proof"},
        )
    finally:
        event.remove(AdministrativeAudit, "before_insert", fail_audit)
    assert response.status_code == 500
    assert response.json()["code"] == "INTERNAL_ERROR"
    for value in (issued["apiKey"], *generated, "INSERT", "Rollback proof"):
        assert value not in response.text
    assert observed == [credential_id]
    assert _snapshot(operator, credential_id) == before
    assert _audit_count(operator) == count
    assert store.authenticate(issued["apiKey"])
    if generated:
        assert store.authenticate(generated[0]) is None


def test_v04_operator_credential_lifecycle_audited_store_requires_reason(operator):
    issued = _issue(operator)
    before = _snapshot(operator, issued["credentialId"])
    with pytest.raises(ReasonRequired):
        operator.app.state.credential_store.revoke(
            UUID(issued["credentialId"]), audit=True, reason="  "
        )
    assert _snapshot(operator, issued["credentialId"]) == before


def test_v04_operator_credential_lifecycle_console_and_operation_ids(operator):
    page = operator.get("/operator")
    for element in (
        "credential-rotate",
        "credential-revoke",
        "credential-revoke-form",
        "credential-revoke-reason",
        "credential-revoke-confirm",
        "credential-revoke-cancel",
    ):
        assert f'id="{element}"' in page.text
    assert "Confirmar revogação" in page.text
    assert "Informe o motivo antes de confirmar." in page.text
    assert "reason.value.trim()" in page.text
    assert "REASON_REQUIRED" in page.text
    assert "CREDENTIAL_REVOKED" in page.text
    assert 'if (action === "rotate")' in page.text
    assert "const { apiKey, ...metadata } = result" in page.text
    assert "localStorage" not in page.text
    assert "sessionStorage" not in page.text
    paths = operator.get("/openapi.json").json()["paths"]
    for action, operation in (
        ("rotate", "rotateCredential"),
        ("revoke", "revokeCredential"),
    ):
        assert (
            paths[f"/v1/credentials/{{credentialId}}/{action}"]["post"]["operationId"]
            == operation
        )
