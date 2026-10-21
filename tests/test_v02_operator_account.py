"""V-02 account operations through the real app and PostgreSQL transactions."""

from __future__ import annotations

from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, func, insert, select
from sqlalchemy.orm import Session

from app.access.models import Account, AdministrativeAudit, Credential
from app.jobs.webhooks import WebhookConfigStore


@pytest.fixture
def operator(client):
    response = client.post(
        "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
    )
    assert response.status_code == 204
    return client


def _counts(client):
    with Session(client.app.state.database_engine) as session:
        return tuple(
            session.scalar(select(func.count()).select_from(model))
            for model in (Account, AdministrativeAudit, Credential)
        )


def test_v02_operator_account_create_commits_account_and_safe_audit(operator):
    response = operator.post("/v1/accounts", json={"name": "  Equipe A  "})

    assert response.status_code == 201
    account = response.json()
    account_id = UUID(account["accountId"])
    assert response.headers["location"] == f"/v1/accounts/{account_id}"
    assert account == {
        "accountId": str(account_id),
        "name": "Equipe A",
        "credentialCount": 0,
        "webhookDestination": {"configured": False, "active": False, "url": None},
    }
    assert _counts(operator) == (1, 1, 0)
    with Session(operator.app.state.database_engine) as session:
        persisted = session.get(Account, account_id)
        audit = session.scalars(select(AdministrativeAudit)).one()
        assert persisted.name == account["name"]
        assert audit.occurred_at.tzinfo is not None
        assert audit.actor == "operational_credential"
        assert audit.account_id == account_id
        assert audit.action == "account_created"
        assert audit.object_type == "account"
        assert audit.object_id == str(account_id)
        assert audit.reason is None
        # Only these facts can be persisted; there is no arbitrary payload field.
        assert set(audit.__table__.columns.keys()) == {
            "id",
            "occurred_at",
            "actor",
            "account_id",
            "action",
            "object_type",
            "object_id",
            "reason",
        }
        assert operator.app.state.operator_secret not in str(audit.__dict__)
    assert operator.get(response.headers["location"]).json() == account
    listing = operator.get("/v1/accounts").json()
    assert listing["data"] == [account]


@pytest.mark.parametrize("name", ["", " ", "\t\n ", "\u2003", " " * 201])
def test_v02_operator_account_blank_name_has_no_effect(operator, name):
    before = _counts(operator)

    response = operator.post("/v1/accounts", json={"name": name})

    assert response.status_code == 422
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json() == {
        "type": "about:blank",
        "title": "Nome obrigatório",
        "status": 422,
        "code": "ACCOUNT_NAME_REQUIRED",
        "detail": "A conta precisa de um nome que a distinga das demais.",
        "instance": "/v1/accounts",
    }
    assert _counts(operator) == before


def test_v02_operator_account_audit_database_failure_rolls_back_account(operator):
    attempted_ids = []

    def fail_audit(mapper, connection, target):
        # The account INSERT has reached PostgreSQL before the audit fails.
        assert (
            connection.scalar(select(Account.id).where(Account.id == target.account_id))
            == target.account_id
        )
        attempted_ids.append(target.account_id)
        # A real NOT NULL violation aborts the transaction, including the account.
        connection.execute(insert(AdministrativeAudit).values(id=uuid4()))

    before = _counts(operator)
    event.listen(AdministrativeAudit, "before_insert", fail_audit)
    try:
        response = operator.post("/v1/accounts", json={"name": "Rollback account"})
    finally:
        event.remove(AdministrativeAudit, "before_insert", fail_audit)

    assert response.status_code == 500
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "INTERNAL_ERROR"
    assert "INSERT" not in response.text
    assert operator.app.state.operator_secret not in response.text
    assert len(attempted_ids) == 1
    assert _counts(operator) == before
    assert operator.get(f"/v1/accounts/{attempted_ids[0]}").status_code == 404
    # A later request can commit normally after the failed transaction.
    assert operator.post("/v1/accounts", json={"name": "Recovered"}).status_code == 201
    assert _counts(operator) == tuple(
        count + delta for count, delta in zip(before, (1, 1, 0))
    )


def test_v02_operator_account_list_empty_and_pagination(operator):
    assert operator.get("/v1/accounts").json() == {
        "data": [],
        "pagination": {"page": 1, "size": 10, "total": 0, "totalPages": 0},
    }
    store = operator.app.state.credential_store
    for number in reversed(range(12)):
        store.create_account(f"Equipe {number:02}")

    first = operator.get("/v1/accounts").json()
    second = operator.get("/v1/accounts?_page=2").json()
    assert first["pagination"] == {"page": 1, "size": 10, "total": 12, "totalPages": 2}
    assert second["pagination"] == {"page": 2, "size": 10, "total": 12, "totalPages": 2}
    assert [item["name"] for item in first["data"] + second["data"]] == [
        f"Equipe {number:02}" for number in range(12)
    ]
    page = operator.get("/v1/accounts?_page=3&_size=5").json()
    assert len(page["data"]) == 2
    assert page["pagination"] == {"page": 3, "size": 5, "total": 12, "totalPages": 3}
    assert operator.get("/v1/accounts?_page=20").json()["data"] == []
    assert len(operator.get("/v1/accounts?_size=100").json()["data"]) == 12
    assert _counts(operator) == (12, 12, 0)


@pytest.mark.parametrize("query", ["_page=0", "_size=0", "_size=101", "_page=text"])
def test_v02_operator_account_invalid_pagination_is_rejected(operator, query):
    response = operator.get(f"/v1/accounts?{query}")

    assert response.status_code == 400
    assert response.json()["code"] == "INVALID_REQUEST"


def test_v02_operator_account_missing_account_is_safe_not_found(operator):
    response = operator.get(f"/v1/accounts/{uuid4()}")

    assert response.status_code == 404
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "NOT_FOUND"
    assert response.json()["detail"] == "O recurso não está disponível."


def test_v02_operator_account_metadata_counts_credentials_without_secrets(operator):
    # Existing CLI provision/issue/revoke methods retain their behavior.
    store = operator.app.state.credential_store
    metadata, key = store.provision_account("Pilot CLI account")
    account_id = UUID(metadata["accountId"])
    second, second_key = store.issue_for_account(account_id)
    store.revoke(UUID(second["credentialId"]))
    webhooks = WebhookConfigStore(
        operator.app.state.database_engine, operator.app.state.webhook_secret_cipher
    )
    webhook_url = operator.app.state.test_source_url
    webhook_secret = webhooks.configure(account_id, webhook_url)

    detail = operator.get(f"/v1/accounts/{account_id}")
    listing = operator.get("/v1/accounts")

    assert detail.status_code == listing.status_code == 200
    assert detail.json()["credentialCount"] == 2
    assert detail.json()["webhookDestination"] == {
        "configured": True,
        "active": True,
        "url": webhook_url,
    }
    assert listing.json()["data"] == [detail.json()]
    for response in (detail, listing):
        for secret in (
            key,
            second_key,
            webhook_secret,
            operator.app.state.operator_secret,
        ):
            assert secret not in response.text
        assert "key_hash" not in response.text
        assert "secret_ciphertext" not in response.text
        assert "apiKey" not in response.text
        assert "verificationMaterial" not in response.text
    assert _counts(operator) == (1, 0, 2)


def test_v02_operator_account_same_name_is_stably_paginated(operator):
    first = operator.post("/v1/accounts", json={"name": "Mesmo nome"}).json()
    second = operator.post("/v1/accounts", json={"name": "Mesmo nome"}).json()
    page_one = operator.get("/v1/accounts?_size=1").json()
    page_two = operator.get("/v1/accounts?_size=1&_page=2").json()

    assert {page_one["data"][0]["accountId"], page_two["data"][0]["accountId"]} == {
        first["accountId"],
        second["accountId"],
    }
    assert operator.get("/v1/accounts?_size=1").json() == page_one


def test_v02_operator_account_customer_key_cannot_create_account(client):
    _, key = client.app.state.credential_store.provision_account("Customer")
    before = _counts(client)

    response = client.post(
        "/v1/accounts", json={"name": "Forbidden"}, headers={"X-API-Key": key}
    )

    assert response.status_code == 401
    assert _counts(client) == before


def test_v02_operator_account_console_serves_creation_and_list_to_operator(operator):
    response = operator.get("/operator")

    assert response.status_code == 200
    assert 'id="account-create"' in response.text
    assert 'id="account-list"' in response.text
    assert 'role="alert"' in response.text
    assert "ACCOUNT_NAME_REQUIRED" in response.text
    operator.delete("/v1/operator-sessions")
    login = operator.get("/operator")
    assert 'id="account-create"' not in login.text
    assert 'id="account-list"' not in login.text
