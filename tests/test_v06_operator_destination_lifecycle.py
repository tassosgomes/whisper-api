"""V-06 material rotation, containment, and atomic administrative audit."""

from __future__ import annotations

import base64
import hashlib
import hmac
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, insert, select
from sqlalchemy.orm import Session

from app.access.models import Account, AdministrativeAudit, Credential
from app.jobs.models import NotificationEvent, TranscriptionJob, WebhookSecret
from app.jobs.webhooks import WebhookReasonRequired, persist_terminal_event


@pytest.fixture
def destination(client, https_webhook_receiver):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    account = client.post("/v1/accounts", json={"name": "Lifecycle owner"}).json()
    owner = client.post(f"/v1/accounts/{account['accountId']}/credentials").json()
    path = f"/v1/accounts/{owner['accountId']}/webhook-destination"
    response = client.put(path, json={"url": https_webhook_receiver.url})
    assert response.status_code == 201
    return owner, path, response.json()["verificationMaterial"]


def _snapshot(client, owner):
    account_id = UUID(owner["accountId"])
    with Session(client.app.state.database_engine) as session:
        account = session.get(Account, account_id)
        result = [account.webhook_url, account.webhook_disabled_at]
        for model in (
            WebhookSecret,
            NotificationEvent,
            AdministrativeAudit,
            Credential,
        ):
            result.append(
                session.execute(
                    select(model.__table__)
                    .where(model.account_id == account_id)
                    .order_by(*model.__table__.primary_key.columns)
                ).all()
            )
        return result


def _terminal(client, owner, now=None):
    now = now or datetime.now(timezone.utc)
    with Session(client.app.state.database_engine) as session, session.begin():
        job = TranscriptionJob(
            id=f"trn_{uuid4().hex}",
            account_id=UUID(owner["accountId"]),
            credential_id=UUID(owner["credentialId"]),
            status="completed",
            accepted_at=now,
            terminal_at=now,
        )
        session.add(job)
        session.flush()
        return persist_terminal_event(session, job, now).event_id


def _verifies(request, material):
    headers = request["headers"]
    content = (
        f"{headers['webhook-id']}.{headers['webhook-timestamp']}.".encode()
        + request["body"]
    )
    key = base64.b64decode(material.removeprefix("whsec_"), validate=True)
    expected = (
        "v1,"
        + base64.b64encode(hmac.new(key, content, hashlib.sha256).digest()).decode()
    )
    return any(
        hmac.compare_digest(expected, signature)
        for signature in headers["webhook-signature"].split()
    )


def _audit(client, owner, action, reason, *materials):
    with Session(client.app.state.database_engine) as session:
        audit = session.scalars(
            select(AdministrativeAudit).where(
                AdministrativeAudit.account_id == UUID(owner["accountId"]),
                AdministrativeAudit.action == action,
            )
        ).one()
        assert audit.actor == "operational_credential"
        assert audit.account_id == UUID(owner["accountId"])
        assert audit.object_type == "webhook_destination"
        assert audit.object_id == owner["accountId"]
        assert audit.reason == reason
        assert audit.occurred_at.tzinfo is not None
        for sensitive in (
            *materials,
            owner["apiKey"],
            client.app.state.operator_secret,
            client.app.state.test_source_url,
        ):
            assert sensitive not in str(audit.__dict__)


def test_v06_operator_destination_lifecycle_planned_overlap_exactly_72_hours(
    client, destination, https_webhook_receiver, monkeypatch
):
    owner, path, old = destination
    now = datetime.now(timezone.utc)
    monkeypatch.setattr("app.jobs.webhooks._utc_now", lambda: now)
    response = client.post(f"{path}/rotate", json={"mode": "planned"})
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    new = response.json()["verificationMaterial"]
    assert new != old
    deadline = now + timedelta(hours=72)
    assert (
        datetime.fromisoformat(
            response.json()["previousMaterialValidUntil"].replace("Z", "+00:00")
        )
        == deadline
    )
    dispatcher = client.app.state.notification_dispatcher
    for index, instant in enumerate(
        (
            now,
            deadline - timedelta(microseconds=1),
            deadline,
            deadline + timedelta(seconds=1),
        )
    ):
        now = instant
        _terminal(client, owner, now)
        assert dispatcher.run_once()
        received = https_webhook_receiver.wait_for_requests(index + 1)[index]
        assert _verifies(received, new)
        assert _verifies(received, old) is (index < 2)
        metadata = client.get(path).json()
        assert bool(metadata["previousMaterialValidUntil"]) is (index < 2)
    for read_path in (path, f"/v1/accounts/{owner['accountId']}", "/v1/accounts"):
        read = client.get(read_path)
        assert "verificationMaterial" not in read.text
        assert old not in read.text and new not in read.text
    _audit(client, owner, "webhook_material_rotated_planned", None, old, new)


@pytest.mark.parametrize("action", ["rotate", "deactivate"])
@pytest.mark.parametrize("reason", [None, "", " \t\n "])
def test_v06_operator_destination_lifecycle_missing_reason_never_calls_mutation(
    client, destination, monkeypatch, action, reason
):
    owner, path, material = destination
    _terminal(client, owner)
    before = _snapshot(client, owner)

    def forbidden(*args, **kwargs):
        pytest.fail("A reasonless operation must not call the store")

    monkeypatch.setattr(
        client.app.state.webhook_config_store,
        "rotate_material" if action == "rotate" else "deactivate_destination",
        forbidden,
    )
    payload = {"mode": "immediate"} if action == "rotate" else {}
    if reason is not None:
        payload["reason"] = reason
    response = client.post(f"{path}/{action}", json=payload)
    assert response.status_code == 422
    assert response.headers["content-type"] == "application/problem+json"
    assert response.json()["code"] == "REASON_REQUIRED"
    assert material not in response.text
    assert _snapshot(client, owner) == before
    assert client.app.state.notification_dispatcher.run_once()


def test_v06_operator_destination_lifecycle_deactivate_without_body(
    client, destination
):
    owner, path, _ = destination
    before = _snapshot(client, owner)
    response = client.post(f"{path}/deactivate")
    assert response.status_code == 422
    assert response.json()["code"] == "REASON_REQUIRED"
    assert _snapshot(client, owner) == before


def test_v06_operator_destination_lifecycle_immediate_revokes_all_previous_material(
    client, destination, https_webhook_receiver
):
    owner, path, original = destination
    planned = client.post(f"{path}/rotate", json={"mode": "planned"}).json()[
        "verificationMaterial"
    ]
    response = client.post(
        f"{path}/rotate", json={"mode": "immediate", "reason": "  material exposto  "}
    )
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    new = response.json()["verificationMaterial"]
    assert new not in (original, planned)
    assert response.json()["previousMaterialValidUntil"] is None
    _terminal(client, owner)
    assert client.app.state.notification_dispatcher.run_once()
    received = https_webhook_receiver.wait_for_requests(1)[0]
    assert _verifies(received, new)
    assert not _verifies(received, original)
    assert not _verifies(received, planned)
    assert "verificationMaterial" not in client.get(path).text
    _audit(
        client,
        owner,
        "webhook_material_rotated_immediate",
        "material exposto",
        original,
        planned,
        new,
    )


def test_v06_operator_destination_lifecycle_deactivate_cancels_pending_and_future(
    client, destination, https_webhook_receiver
):
    owner, path, material = destination
    # Leave a retry pending and a second event not yet attempted.
    https_webhook_receiver.respond_with(503)
    retry = _terminal(client, owner)
    assert client.app.state.notification_dispatcher.run_once()
    pending = _terminal(client, owner)
    client.post(f"{path}/rotate", json={"mode": "planned"})
    response = client.post(
        f"{path}/deactivate", json={"reason": "  destino comprometido "}
    )
    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert response.json() == {
        "configured": False,
        "active": False,
        "url": None,
        "previousMaterialValidUntil": None,
    }
    assert client.get(path).json() == response.json()
    assert (
        client.get(f"/v1/accounts/{owner['accountId']}").json()["webhookDestination"][
            "active"
        ]
        is False
    )
    future = _terminal(client, owner)
    assert not client.app.state.notification_dispatcher.run_once()
    assert len(https_webhook_receiver.requests) == 1
    with Session(client.app.state.database_engine) as session:
        assert not session.scalars(select(WebhookSecret)).all()
        for event_id in (retry, pending):
            notification = session.get(NotificationEvent, event_id)
            assert notification.status == "disabled"
            assert notification.payload == b""
            assert notification.next_attempt_at is None
            assert notification.lease_owner is None
        assert session.get(NotificationEvent, future).status == "not_configured"
    _audit(
        client,
        owner,
        "webhook_destination_deactivated",
        "destino comprometido",
        material,
    )


@pytest.mark.parametrize("action", ["planned", "immediate", "deactivate"])
def test_v06_operator_destination_lifecycle_audit_failure_rolls_back_everything(
    client, destination, action
):
    owner, path, material = destination
    _terminal(client, owner)
    before = _snapshot(client, owner)
    observed = []

    def fail_audit(mapper, connection, target):
        if target.action.startswith(
            ("webhook_material_rotated", "webhook_destination_deactivated")
        ):
            # The mutation must have reached the SAME transaction before the audit fails.
            if action == "deactivate":
                assert (
                    connection.scalar(
                        select(Account.webhook_disabled_at).where(
                            Account.id == UUID(owner["accountId"])
                        )
                    )
                    is not None
                )
                assert not connection.execute(select(WebhookSecret.id)).all()
                assert connection.scalar(select(NotificationEvent.status)) == "disabled"
            else:
                assert len(connection.execute(select(WebhookSecret.id)).all()) == 2
            observed.append(target.action)
            connection.execute(insert(AdministrativeAudit).values(id=uuid4()))

    event.listen(AdministrativeAudit, "before_insert", fail_audit)
    try:
        response = client.post(
            f"{path}/{'deactivate' if action == 'deactivate' else 'rotate'}",
            json={
                "reason": "containment",
                **({} if action == "deactivate" else {"mode": action}),
            },
        )
    finally:
        event.remove(AdministrativeAudit, "before_insert", fail_audit)
    assert response.status_code == 500
    assert response.json()["code"] == "INTERNAL_ERROR"
    assert len(observed) == 1
    assert _snapshot(client, owner) == before
    for sensitive in (
        material,
        "whsec_",
        "verificationMaterial",
        "INSERT",
        owner["apiKey"],
    ):
        assert sensitive not in response.text
    assert client.app.state.notification_dispatcher.run_once()


def test_v06_operator_destination_lifecycle_api_key_and_material_are_independent(
    client, destination
):
    owner, path, _ = destination
    before = _snapshot(client, owner)
    response = client.post(f"{path}/rotate", json={"mode": "planned"})
    assert response.status_code == 200
    assert _snapshot(client, owner)[5] == before[5]
    store = client.app.state.credential_store
    assert store.authenticate(owner["apiKey"]) is not None
    destination_before = _snapshot(client, owner)[:3]
    rotated = client.post(f"/v1/credentials/{owner['credentialId']}/rotate")
    assert rotated.status_code == 200
    assert _snapshot(client, owner)[:3] == destination_before
    assert store.authenticate(owner["apiKey"]) is None
    assert store.authenticate(rotated.json()["apiKey"]) is not None


@pytest.mark.parametrize("action", ["rotate", "deactivate"])
def test_v06_operator_destination_lifecycle_machine_key_cannot_mutate(
    client, destination, action
):
    owner, path, _ = destination
    before = _snapshot(client, owner)
    client.cookies.clear()
    response = client.post(
        f"{path}/{action}",
        headers={"X-API-Key": owner["apiKey"]},
        json={
            "reason": "containment",
            **({"mode": "immediate"} if action == "rotate" else {}),
        },
    )
    assert response.status_code == 401
    assert response.json()["code"] == "UNAUTHORIZED"
    assert _snapshot(client, owner) == before


@pytest.mark.parametrize("state", ["absent", "inactive", "unknown_account"])
@pytest.mark.parametrize("action", ["rotate", "deactivate"])
def test_v06_operator_destination_lifecycle_missing_destination(
    client, destination, state, action
):
    owner, path, _ = destination
    if state == "absent":
        account = client.post(
            "/v1/accounts", json={"name": "Without destination"}
        ).json()
        path = f"/v1/accounts/{account['accountId']}/webhook-destination"
    elif state == "inactive":
        assert (
            client.post(f"{path}/deactivate", json={"reason": "retired"}).status_code
            == 200
        )
    else:
        path = f"/v1/accounts/{uuid4()}/webhook-destination"
    before = _snapshot(client, owner)
    response = client.post(
        f"{path}/{action}",
        json={
            "reason": "containment",
            **({"mode": "planned"} if action == "rotate" else {}),
        },
    )
    assert response.status_code == 404
    assert response.json()["code"] == (
        "DESTINATION_NOT_FOUND"
        if action == "rotate" and state != "unknown_account"
        else "NOT_FOUND"
    )
    assert _snapshot(client, owner) == before


def test_v06_operator_destination_lifecycle_domain_requires_reason(client, destination):
    owner, _, _ = destination
    before = _snapshot(client, owner)
    store = client.app.state.webhook_config_store
    with pytest.raises(WebhookReasonRequired):
        store.rotate_material(UUID(owner["accountId"]), immediate=True, reason="  ")
    with pytest.raises(WebhookReasonRequired):
        store.deactivate_destination(UUID(owner["accountId"]), None)
    assert _snapshot(client, owner) == before


def test_v06_operator_destination_lifecycle_console_and_operation_ids(
    client, destination
):
    page = client.get("/operator")
    assert page.status_code == 200
    for control in (
        "destination-rotate-planned",
        "destination-rotate-immediate",
        "destination-deactivate",
        "destination-reason-form",
        "destination-reason",
        "destination-cancel",
    ):
        assert f'id="{control}"' in page.text
    assert 'name="reason" required' in page.text
    assert 'requestDestinationReason("immediate")' in page.text
    assert 'requestDestinationReason("deactivate")' in page.text
    assert "if (!reason)" in page.text
    assert "mutateDestination(destinationAction, reason)" in page.text
    schema = client.get("/openapi.json").json()["paths"]
    _, path, _ = destination
    contract_path = path.replace(destination[0]["accountId"], "{accountId}")
    assert (
        schema[f"{contract_path}/rotate"]["post"]["operationId"]
        == "rotateWebhookMaterial"
    )
    assert (
        schema[f"{contract_path}/deactivate"]["post"]["operationId"]
        == "deactivateWebhookDestination"
    )
