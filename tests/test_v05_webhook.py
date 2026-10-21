"""V-05 terminal webhook signing, delivery policy, and end-to-end smoke."""

from __future__ import annotations

import io
import json
import time
import wave
from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import select, update
from sqlalchemy.orm import Session

from app.access.models import Account
from app.jobs.models import NotificationEvent, TranscriptionJob, WebhookSecret
from app.jobs.webhooks import (
    NotificationDispatcher,
    WebhookConfigStore,
    WebhookConfigurationError,
    persist_terminal_event,
)


def _provision(client, receiver) -> tuple[dict, str, str]:
    metadata, api_key = client.app.state.credential_store.provision_account(
        f"V-05 {uuid4().hex}"
    )
    cipher = client.app.state.webhook_secret_cipher
    assert cipher is not None
    config = WebhookConfigStore(client.app.state.database_engine, cipher)
    secret = config.configure(UUID(metadata["accountId"]), receiver.url)
    receiver.allow_secret(secret)
    return metadata, api_key, secret


def _create_event(
    client,
    metadata: dict,
    *,
    status: str = "completed",
    client_reference: str | None = "asset-55",
) -> tuple[str, str]:
    now = datetime.now(timezone.utc)
    job_id = f"trn_{uuid4().hex}"
    job = TranscriptionJob(
        id=job_id,
        account_id=UUID(metadata["accountId"]),
        credential_id=UUID(metadata["credentialId"]),
        status=status,
        client_reference=client_reference,
        accepted_at=now,
        terminal_at=now,
        failure_code="SOURCE_UNAVAILABLE" if status == "failed" else None,
    )
    with Session(client.app.state.database_engine) as session, session.begin():
        session.add(job)
        session.flush()
        event = persist_terminal_event(session, job, now)
        event_id = event.event_id
    return job_id, event_id


def _load_event(client, event_id: str) -> NotificationEvent:
    with Session(client.app.state.database_engine) as session:
        return session.get(NotificationEvent, event_id)


def _dispatch(client) -> NotificationDispatcher:
    return client.app.state.notification_dispatcher


def _wav_bytes() -> bytes:
    destination = io.BytesIO()
    with wave.open(destination, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        audio.writeframes(b"\x00\x00" * 16000)
    return destination.getvalue()


def _wait_for_job(client, job_id: str, api_key: str, expected: str) -> dict:
    deadline = time.monotonic() + 10
    while time.monotonic() < deadline:
        response = client.get(
            f"/v1/transcriptions/{job_id}", headers={"X-API-Key": api_key}
        )
        assert response.status_code == 200, response.text
        body = response.json()
        if body["status"] == expected:
            return body
        time.sleep(0.02)
    pytest.fail(f"Job {job_id} não alcançou o estado {expected}.")


def test_v05_webhook_signs_exact_minimal_payload_and_stable_id(
    client, https_webhook_receiver
):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    job_id, event_id = _create_event(client, metadata, client_reference="asset-55")

    assert _dispatch(client).run_once() is True
    requests = https_webhook_receiver.wait_for_requests(1)
    assert len(requests) == 1
    received = requests[0]
    payload = json.loads(received["body"])
    assert payload == {
        "eventId": event_id,
        "jobId": job_id,
        "status": "completed",
        "clientReference": "asset-55",
    }
    assert received["headers"]["webhook-id"] == event_id
    assert received["valid"] is True
    assert received["duplicate"] is False
    assert b"sourceUrl" not in received["body"]
    assert b"transcription" not in received["body"]
    assert _load_event(client, event_id).status == "delivered"


def test_v05_webhook_retries_with_fresh_timestamp_and_rotation_signatures(
    client, https_webhook_receiver
):
    metadata, _, old_secret = _provision(client, https_webhook_receiver)
    new_secret = WebhookConfigStore(
        client.app.state.database_engine, client.app.state.webhook_secret_cipher
    ).rotate(UUID(metadata["accountId"]))
    https_webhook_receiver.allow_secret(new_secret)
    https_webhook_receiver.respond_with(503, 200)
    _, event_id = _create_event(client, metadata)

    assert _dispatch(client).run_once() is True
    first = https_webhook_receiver.wait_for_requests(1)[0]
    with Session(client.app.state.database_engine) as session, session.begin():
        session.execute(
            update(NotificationEvent)
            .where(NotificationEvent.event_id == event_id)
            .values(next_attempt_at=datetime.now(timezone.utc))
        )
    assert _dispatch(client).run_once() is True
    second = https_webhook_receiver.wait_for_requests(2)[1]

    assert first["valid"] is True and second["valid"] is True
    assert first["headers"]["webhook-id"] == second["headers"]["webhook-id"] == event_id
    assert int(second["headers"]["webhook-timestamp"]) > int(
        first["headers"]["webhook-timestamp"]
    )
    assert len(first["headers"]["webhook-signature"].split()) == 2
    assert len(second["headers"]["webhook-signature"].split()) == 2
    assert second["duplicate"] is True
    assert _load_event(client, event_id).status == "delivered"
    assert old_secret != new_secret

    revoked_secret = WebhookConfigStore(
        client.app.state.database_engine, client.app.state.webhook_secret_cipher
    ).rotate(UUID(metadata["accountId"]), immediate=True)
    https_webhook_receiver.allow_secret(revoked_secret)
    _, immediate_event = _create_event(client, metadata)
    assert _dispatch(client).run_once() is True
    immediate = https_webhook_receiver.wait_for_requests(3)[2]

    assert immediate["valid"] is True
    assert len(immediate["headers"]["webhook-signature"].split()) == 1
    assert _load_event(client, immediate_event).status == "delivered"


def test_v05_webhook_does_not_follow_redirects(client, https_webhook_receiver):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    https_webhook_receiver.respond_with(
        302, redirect_location="http://127.0.0.1:1/private"
    )
    _, event_id = _create_event(client, metadata)

    assert _dispatch(client).run_once() is True
    requests = https_webhook_receiver.wait_for_requests(1)

    assert len(requests) == 1
    assert _load_event(client, event_id).status == "failed"
    assert _load_event(client, event_id).last_error_code == "redirect_rejected"


def test_v05_webhook_410_disables_destination_and_pending_events(
    client, https_webhook_receiver
):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    _, first_event = _create_event(client, metadata)
    _, second_event = _create_event(client, metadata)
    https_webhook_receiver.respond_with(410)

    assert _dispatch(client).run_once() is True

    with Session(client.app.state.database_engine) as session:
        events = {
            event.event_id: event.status
            for event in session.scalars(
                select(NotificationEvent).where(
                    NotificationEvent.event_id.in_((first_event, second_event))
                )
            )
        }
        account = session.get(Account, UUID(metadata["accountId"]))
        assert account.webhook_disabled_at is not None
    assert events == {first_event: "disabled", second_event: "disabled"}


def test_v05_webhook_429_respects_retry_after(client, https_webhook_receiver):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    _, event_id = _create_event(client, metadata)
    https_webhook_receiver.respond_with(429, retry_after=("4",))
    started = datetime.now(timezone.utc)

    assert _dispatch(client).run_once() is True

    event = _load_event(client, event_id)
    assert event.status == "pending"
    assert event.last_status_code == 429
    assert event.next_attempt_at >= started + timedelta(seconds=3.8)


def test_v05_webhook_rejects_private_callback_address(client, https_webhook_receiver):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    store = WebhookConfigStore(
        client.app.state.database_engine, client.app.state.webhook_secret_cipher
    )

    with pytest.raises(WebhookConfigurationError):
        store.set_endpoint(UUID(metadata["accountId"]), "https://127.0.0.1/hook")


def test_v05_webhook_exhausts_72_hour_window_without_changing_job(
    client, https_webhook_receiver
):
    metadata, _, _ = _provision(client, https_webhook_receiver)
    job_id, event_id = _create_event(client, metadata)
    with Session(client.app.state.database_engine) as session, session.begin():
        session.execute(
            update(NotificationEvent)
            .where(NotificationEvent.event_id == event_id)
            .values(retry_deadline=datetime.now(timezone.utc) - timedelta(seconds=1))
        )

    assert _dispatch(client).run_once() is False

    with Session(client.app.state.database_engine) as session:
        event = session.get(NotificationEvent, event_id)
        job = session.get(TranscriptionJob, job_id)
    assert event.status == "exhausted"
    assert job.status == "completed"


def test_v05_webhook_signing_keys_are_account_scoped_and_api_rotation_independent(
    client, https_webhook_receiver
):
    metadata, _, webhook_secret = _provision(client, https_webhook_receiver)
    sibling, _ = client.app.state.credential_store.provision_account(
        f"V-05 sibling {uuid4().hex}"
    )
    sibling_secret = WebhookConfigStore(
        client.app.state.database_engine, client.app.state.webhook_secret_cipher
    ).configure(UUID(sibling["accountId"]), https_webhook_receiver.url)
    https_webhook_receiver.respond_with(200)
    _, event_id = _create_event(client, metadata)
    client.app.state.credential_store.rotate(UUID(metadata["credentialId"]))

    assert _dispatch(client).run_once() is True
    received = https_webhook_receiver.wait_for_requests(1)[0]

    assert webhook_secret != sibling_secret
    assert received["valid"] is True
    assert all(
        client.app.state.webhook_secret_cipher.decrypt(
            UUID(metadata["accountId"]), row.secret_ciphertext
        )
        == webhook_secret
        for row in _secret_rows(client, UUID(metadata["accountId"]))
        if row.is_current
    )
    assert _load_event(client, event_id).status == "delivered"


def _secret_rows(client, account_id: UUID) -> list[WebhookSecret]:
    with Session(client.app.state.database_engine) as session:
        return list(
            session.scalars(
                select(WebhookSecret).where(WebhookSecret.account_id == account_id)
            )
        )


def test_v05_webhook_smoke_creates_processes_and_reads_completed_and_failed_jobs(
    https_webhook_receiver, all_roles_client
):
    client = all_roles_client
    metadata, api_key = client.app.state.credential_store.provision_account(
        f"V-05 journey {uuid4().hex}"
    )
    secret = WebhookConfigStore(
        client.app.state.database_engine, client.app.state.webhook_secret_cipher
    ).configure(UUID(metadata["accountId"]), https_webhook_receiver.url)
    https_webhook_receiver.allow_secret(secret)
    origin = client.app.state.test_source
    origin.body = _wav_bytes()

    completed_response = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": uuid4().hex},
        json={"sourceUrl": origin.url_for("/complete.wav"), "clientReference": "ok"},
    )
    assert completed_response.status_code == 202, completed_response.text
    completed = completed_response.json()
    completed_job = _wait_for_job(client, completed["jobId"], api_key, "completed")
    result = client.get(
        f"/v1/transcriptions/{completed['jobId']}/result",
        headers={"X-API-Key": api_key},
    )
    assert result.status_code == 200, result.text
    assert result.json()["jobId"] == completed["jobId"]

    origin.body = b"not an audio file"
    failed_response = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": uuid4().hex},
        json={"sourceUrl": origin.url_for("/broken.wav"), "clientReference": "bad"},
    )
    assert failed_response.status_code == 202, failed_response.text
    failed = failed_response.json()
    failed_job = _wait_for_job(client, failed["jobId"], api_key, "failed")
    unavailable = client.get(
        f"/v1/transcriptions/{failed['jobId']}/result",
        headers={"X-API-Key": api_key},
    )
    assert unavailable.status_code == 409
    requests = https_webhook_receiver.wait_for_requests(2)
    assert len(requests) == 2
    by_job = {json.loads(request["body"])["jobId"]: request for request in requests}

    assert completed_job["status"] == "completed"
    assert failed_job["status"] == "failed"
    assert json.loads(by_job[completed["jobId"]]["body"])["status"] == "completed"
    assert json.loads(by_job[failed["jobId"]]["body"])["status"] == "failed"
    assert all(request["valid"] for request in requests)
    assert all(b"segments" not in request["body"] for request in requests)
