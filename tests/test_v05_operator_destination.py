"""V-05 operator destination configuration, delivery, and atomic audit."""

from __future__ import annotations

import socket
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, insert, select
from sqlalchemy.orm import Session

from app.access.models import Account, AdministrativeAudit
from app.jobs.models import NotificationEvent, TranscriptionJob, WebhookSecret
from app.jobs.webhooks import persist_terminal_event


def _login(client):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    return client


@pytest.fixture
def operator(client):
    return _login(client)


@pytest.fixture
def trusted_operator(monkeypatch, request):
    monkeypatch.setenv("TRUSTED_PRIVATE_WEBHOOKS", "http://10.20.0.5:9000")
    # Configure the allowlist before the real application lifespan starts.
    return _login(request.getfixturevalue("client"))


def _owner(operator):
    response = operator.post("/v1/accounts", json={"name": "Webhook owner"})
    assert response.status_code == 201
    account = response.json()
    issued = operator.post(f"/v1/accounts/{account['accountId']}/credentials")
    assert issued.status_code == 201
    return issued.json()


def _path(owner):
    return f"/v1/accounts/{owner['accountId']}/webhook-destination"


def _snapshot(operator, owner):
    account_id = UUID(owner["accountId"])
    with Session(operator.app.state.database_engine) as session:
        account = session.get(Account, account_id)
        secrets = session.execute(
            select(WebhookSecret.__table__)
            .where(WebhookSecret.account_id == account_id)
            .order_by(WebhookSecret.id)
        ).all()
        audits = session.execute(
            select(AdministrativeAudit.__table__)
            .where(AdministrativeAudit.account_id == account_id)
            .order_by(AdministrativeAudit.id)
        ).all()
        return account.webhook_url, account.webhook_disabled_at, secrets, audits


def _terminal(operator, owner):
    response = operator.post(
        "/v1/transcriptions",
        headers={"X-API-Key": owner["apiKey"], "Idempotency-Key": str(uuid4())},
        json={"sourceUrl": operator.app.state.test_source_url},
    )
    assert response.status_code == 202
    now = datetime.now(timezone.utc)
    with Session(operator.app.state.database_engine) as session, session.begin():
        job = session.get(TranscriptionJob, response.json()["jobId"])
        job.status = "completed"
        job.terminal_at = now
        session.flush()
        notification = persist_terminal_event(session, job, now)
        return notification.event_id


def _assert_audit(operator, owner, action, material):
    with Session(operator.app.state.database_engine) as session:
        audit = session.scalars(
            select(AdministrativeAudit).where(
                AdministrativeAudit.account_id == UUID(owner["accountId"]),
                AdministrativeAudit.action == action,
            )
        ).one()
        assert audit.actor == "operational_credential"
        assert audit.object_type == "webhook_destination"
        assert audit.object_id == owner["accountId"]
        assert audit.occurred_at.tzinfo is not None
        assert audit.reason is None
        for sensitive in (
            material,
            owner["apiKey"],
            operator.app.state.operator_secret,
            operator.app.state.test_source_url,
        ):
            assert sensitive not in str(audit.__dict__)


def test_v05_operator_destination_creation_delivers_signed_terminal(
    operator, https_webhook_receiver
):
    owner = _owner(operator)
    path = _path(owner)
    response = operator.put(path, json={"url": https_webhook_receiver.url})
    assert response.status_code == 201
    assert response.headers["location"] == path
    assert response.headers["cache-control"] == "no-store"
    destination = response.json()
    material = destination.pop("verificationMaterial")
    assert material.startswith("whsec_")
    assert destination == {
        "configured": True,
        "active": True,
        "url": https_webhook_receiver.url,
        "previousMaterialValidUntil": None,
    }
    https_webhook_receiver.allow_secret(material)
    event_id = _terminal(operator, owner)
    assert operator.app.state.notification_dispatcher.run_once()
    received = https_webhook_receiver.wait_for_requests(1)
    assert len(received) == 1
    assert received[0]["valid"]
    assert received[0]["path"] == "/terminal"
    assert received[0]["headers"]["webhook-id"] == event_id
    for read_path in (path, f"/v1/accounts/{owner['accountId']}", "/v1/accounts"):
        read = operator.get(read_path)
        assert read.status_code == 200
        assert read.headers["cache-control"] == "no-store"
        assert material not in read.text
        assert "verificationMaterial" not in read.text
    assert operator.get(path).json() == destination
    _, _, rows, _ = _snapshot(operator, owner)
    assert len(rows) == 1
    assert material.encode() not in rows[0].secret_ciphertext
    assert (
        operator.app.state.webhook_secret_cipher.decrypt(
            UUID(owner["accountId"]), rows[0].secret_ciphertext
        )
        == material
    )
    _assert_audit(operator, owner, "webhook_destination_created", material)


@pytest.mark.parametrize("overlap", [False, True])
def test_v05_operator_destination_update_preserves_material_and_changes_delivery(
    operator, https_webhook_receiver, overlap
):
    owner = _owner(operator)
    path = _path(owner)
    initial = operator.put(path, json={"url": https_webhook_receiver.url})
    material = initial.json()["verificationMaterial"]
    https_webhook_receiver.allow_secret(material)
    _terminal(operator, owner)
    assert operator.app.state.notification_dispatcher.run_once()
    assert https_webhook_receiver.wait_for_requests(1)[0]["path"] == "/terminal"
    # Previous overlapping material must also survive an address-only update.
    if overlap:
        operator.app.state.webhook_config_store.rotate(UUID(owner["accountId"]))
    before = _snapshot(operator, owner)
    new_url = https_webhook_receiver.url.replace("/terminal", "/new-destination")
    response = operator.put(path, json={"url": new_url})
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert response.json()["url"] == new_url
    assert response.json()["active"] is True
    assert bool(response.json()["previousMaterialValidUntil"]) is overlap
    assert "verificationMaterial" not in response.text
    assert material not in response.text
    after = _snapshot(operator, owner)
    assert after[2] == before[2]
    assert len(after[3]) == len(before[3]) + 1
    _terminal(operator, owner)
    assert operator.app.state.notification_dispatcher.run_once()
    received = https_webhook_receiver.wait_for_requests(2)
    assert len(received) == 2
    assert received[1]["path"] == "/new-destination"
    assert received[1]["valid"]  # Verifies using the material from creation.
    assert operator.get(path).json() == response.json()
    _assert_audit(operator, owner, "webhook_destination_updated", material)


@pytest.mark.parametrize("configured", [False, True])
@pytest.mark.parametrize(
    "url",
    [
        "",
        "not-a-url",
        "http://media.example.test/events",
        "https://127.0.0.1/events",
        "https://169.254.169.254/events",
        "https://10.20.0.5/events",
    ],
)
def test_v05_operator_destination_rejection_has_no_effect(operator, configured, url):
    owner = _owner(operator)
    path = _path(owner)
    if configured:
        assert (
            operator.put(
                path, json={"url": "https://media.example.test/events"}
            ).status_code
            == 201
        )
    before = _snapshot(operator, owner)
    response = operator.put(path, json={"url": url})
    assert response.status_code == 422
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "DESTINATION_REJECTED"
    assert _snapshot(operator, owner) == before


def test_v05_operator_destination_uses_webhook_allowlist(trusted_operator):
    owner = _owner(trusted_operator)
    path = _path(owner)
    url = "http://10.20.0.5:9000/events"
    response = trusted_operator.put(path, json={"url": url})
    assert response.status_code == 201
    assert response.json()["url"] == url
    assert response.json()["verificationMaterial"].startswith("whsec_")
    before = _snapshot(trusted_operator, owner)
    # Allowing a host and port must not allow other ports, schemes, or metadata.
    for rejected in (
        "http://10.20.0.5:9001/events",
        "https://10.20.0.5:9000/events",
        "http://169.254.169.254:9000/events",
    ):
        refused = trusted_operator.put(path, json={"url": rejected})
        assert refused.status_code == 422
        assert refused.json()["code"] == "DESTINATION_REJECTED"
        assert _snapshot(trusted_operator, owner) == before


def test_v05_operator_destination_mixed_dns_is_rejected(operator, monkeypatch):
    owner = _owner(operator)
    before = _snapshot(operator, owner)
    original = socket.getaddrinfo

    def mixed(host, port, *args, **kwargs):
        if host == "mixed.example.test":
            return [
                (socket.AF_INET, socket.SOCK_STREAM, socket.IPPROTO_TCP, "", (ip, port))
                for ip in ("93.184.216.34", "10.20.0.5")
            ]
        return original(host, port, *args, **kwargs)

    monkeypatch.setattr(socket, "getaddrinfo", mixed)
    response = operator.put(
        _path(owner), json={"url": "https://mixed.example.test/events"}
    )
    assert response.status_code == 422
    assert response.json()["code"] == "DESTINATION_REJECTED"
    assert _snapshot(operator, owner) == before


def test_v05_operator_destination_absent_keeps_public_api_and_sends_no_notice(
    operator, https_webhook_receiver
):
    owner = _owner(operator)
    response = operator.get(_path(owner))
    assert response.status_code == 404
    assert response.json()["code"] == "DESTINATION_NOT_FOUND"
    event_id = _terminal(operator, owner)
    assert not operator.app.state.notification_dispatcher.run_once()
    assert https_webhook_receiver.requests == []
    with Session(operator.app.state.database_engine) as session:
        notification = session.get(NotificationEvent, event_id)
        assert notification.status == "not_configured"
        assert notification.attempt_count == 0
        assert notification.payload == b""
    assert operator.app.state.credential_store.authenticate(owner["apiKey"])


@pytest.mark.parametrize("method", ["get", "put"])
def test_v05_operator_destination_unknown_account(operator, method):
    response = getattr(operator, method)(
        f"/v1/accounts/{uuid4()}/webhook-destination",
        **(
            {"json": {"url": "https://media.example.test/events"}}
            if method == "put"
            else {}
        ),
    )
    assert response.status_code == 404
    assert response.json()["code"] == "NOT_FOUND"


@pytest.mark.parametrize("method", ["get", "put"])
def test_v05_operator_destination_customer_key_cannot_access(client, method):
    owner, key = client.app.state.credential_store.provision_account("Customer")
    before = _snapshot(client, owner)
    response = getattr(client, method)(
        _path(owner),
        headers={"X-API-Key": key},
        **(
            {"json": {"url": "https://media.example.test/events"}}
            if method == "put"
            else {}
        ),
    )
    assert response.status_code == 401
    assert response.json()["code"] == "UNAUTHORIZED"
    assert _snapshot(client, owner) == before


@pytest.mark.parametrize("configured", [False, True])
def test_v05_operator_destination_audit_failure_rolls_back(
    operator, monkeypatch, configured
):
    owner = _owner(operator)
    path = _path(owner)
    if configured:
        assert (
            operator.put(
                path, json={"url": "https://media.example.test/old"}
            ).status_code
            == 201
        )
    before = _snapshot(operator, owner)
    url = "https://media.example.test/new"
    observed = []

    def fail_audit(mapper, connection, target):
        if target.object_type == "webhook_destination":
            assert (
                connection.scalar(
                    select(Account.webhook_url).where(
                        Account.id == UUID(owner["accountId"])
                    )
                )
                == url
            )
            assert (
                connection.scalar(
                    select(WebhookSecret.id).where(
                        WebhookSecret.account_id == UUID(owner["accountId"]),
                        WebhookSecret.is_current.is_(True),
                    )
                )
                is not None
            )
            observed.append(target.object_id)
            connection.execute(insert(AdministrativeAudit).values(id=uuid4()))

    event.listen(AdministrativeAudit, "before_insert", fail_audit)
    try:
        response = operator.put(path, json={"url": url})
    finally:
        event.remove(AdministrativeAudit, "before_insert", fail_audit)
    assert response.status_code == 500
    assert response.json()["code"] == "INTERNAL_ERROR"
    for secret in ("whsec_", "verificationMaterial", "INSERT", url, owner["apiKey"]):
        assert secret not in response.text
    assert observed == [owner["accountId"]]
    assert _snapshot(operator, owner) == before


def test_v05_operator_destination_concurrent_put_creates_only_one_material(operator):
    owner = _owner(operator)
    path = _path(owner)

    def put(index):
        return operator.put(path, json={"url": f"https://media.example.test/{index}"})

    with ThreadPoolExecutor(max_workers=2) as pool:
        responses = list(pool.map(put, range(2)))
    assert sorted(response.status_code for response in responses) == [200, 201]
    material = next(
        response.json()["verificationMaterial"]
        for response in responses
        if response.status_code == 201
    )
    _, _, rows, audits = _snapshot(operator, owner)
    assert len(rows) == 1
    assert len(audits) == 4  # Account, credential, creation, and address update.
    assert (
        operator.app.state.webhook_secret_cipher.decrypt(
            UUID(owner["accountId"]), rows[0].secret_ciphertext
        )
        == material
    )


def test_v05_operator_destination_missing_cipher_fails_without_writes(
    operator, monkeypatch
):
    owner = _owner(operator)
    before = _snapshot(operator, owner)
    monkeypatch.setattr(operator.app.state.webhook_config_store, "_cipher", None)
    response = operator.put(
        _path(owner), json={"url": "https://media.example.test/events"}
    )
    assert response.status_code == 500
    assert response.json()["code"] == "INTERNAL_ERROR"
    assert "WEBHOOK_SECRET_ENCRYPTION_KEY" not in response.text
    assert _snapshot(operator, owner) == before


def test_v05_operator_destination_console_and_operation_ids(operator):
    page = operator.get("/operator")
    for element in (
        "destination-form",
        "destination-url",
        "destination-detail",
        "destination-status",
        "destination-error",
        "destination-refresh",
        "destination-material",
        "destination-copy",
        "destination-dismiss",
    ):
        assert f'id="{element}"' in page.text
    assert "Esta é a única exibição." in page.text
    assert "const { verificationMaterial, ...metadata } = result" in page.text
    assert 'document.getElementById("destination-material").value = ""' in page.text
    assert 'window.addEventListener("pagehide", clearDestinationSecret)' in page.text
    assert "DESTINATION_REJECTED" in page.text
    assert "DESTINATION_NOT_FOUND" in page.text
    assert "localStorage" not in page.text
    assert "sessionStorage" not in page.text
    paths = operator.get("/openapi.json").json()["paths"]
    destination = paths["/v1/accounts/{accountId}/webhook-destination"]
    assert destination["get"]["operationId"] == "getWebhookDestination"
    assert destination["put"]["operationId"] == "putWebhookDestination"
