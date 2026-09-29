"""V-05 terminal webhook signing, delivery policy, and end-to-end smoke."""

from __future__ import annotations

import base64
import hashlib
import hmac
import io
import json
import socket
import ssl
import threading
import time
import wave
from datetime import datetime, timedelta, timezone
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
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

TEST_SOURCE_IP = "93.184.216.34"
TEST_WEBHOOK_HOST = "webhook.example.test"


class ControlledWebhookReceiver:
    def __init__(self, certificate: str, private_key: str):
        self._lock = threading.RLock()
        self._requests: list[dict] = []
        self._accepted_secrets: list[str] = []
        self._statuses: list[int] = [200]
        self._retry_after: list[str | None] = []
        self._redirect_location: str | None = None
        self._seen_ids: set[str] = set()

        receiver = self

        class Handler(BaseHTTPRequestHandler):
            def do_POST(self):
                content_length = int(self.headers.get("Content-Length", "0"))
                raw_body = self.rfile.read(content_length)
                headers = {key.lower(): value for key, value in self.headers.items()}
                valid, duplicate = receiver._verify(headers, raw_body)
                with receiver._lock:
                    request_index = len(receiver._requests)
                    receiver._requests.append(
                        {
                            "headers": headers,
                            "body": raw_body,
                            "valid": valid,
                            "duplicate": duplicate,
                        }
                    )
                    status_index = min(request_index, len(receiver._statuses) - 1)
                    status_code = receiver._statuses[status_index]
                    retry_after = (
                        receiver._retry_after[request_index]
                        if request_index < len(receiver._retry_after)
                        else None
                    )
                    location = receiver._redirect_location
                self.send_response(status_code)
                if retry_after is not None:
                    self.send_header("Retry-After", retry_after)
                if location is not None:
                    self.send_header("Location", location)
                self.send_header("Content-Length", "0")
                self.end_headers()

            def log_message(self, format, *args):
                return

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(certificate, private_key)
        server.socket = context.wrap_socket(server.socket, server_side=True)
        self._server = server
        self._thread = threading.Thread(target=server.serve_forever, daemon=True)
        self._thread.start()
        self.url = f"https://{TEST_WEBHOOK_HOST}:{server.server_address[1]}/terminal"

    def close(self):
        self._server.shutdown()
        self._server.server_close()
        self._thread.join(timeout=5)

    @property
    def requests(self) -> list[dict]:
        with self._lock:
            return list(self._requests)

    def allow_secret(self, secret: str) -> None:
        with self._lock:
            self._accepted_secrets.append(secret)

    def respond_with(
        self,
        *statuses: int,
        retry_after: tuple[str | None, ...] = (),
        redirect_location: str | None = None,
    ) -> None:
        with self._lock:
            self._statuses = list(statuses) or [200]
            self._retry_after = list(retry_after)
            self._redirect_location = redirect_location

    def wait_for_requests(self, count: int, timeout: float = 5.0) -> list[dict]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            requests = self.requests
            if len(requests) >= count:
                return requests
            time.sleep(0.01)
        return self.requests

    def _verify(self, headers: dict[str, str], raw_body: bytes) -> tuple[bool, bool]:
        event_id = headers.get("webhook-id", "")
        timestamp = headers.get("webhook-timestamp", "")
        try:
            payload = json.loads(raw_body)
            event_time = int(timestamp)
        except (ValueError, TypeError, json.JSONDecodeError):
            return False, False
        if payload.get("eventId") != event_id or abs(time.time() - event_time) > 300:
            return False, False
        signatures = headers.get("webhook-signature", "").split()
        signed_content = f"{event_id}.{timestamp}.".encode("ascii") + raw_body
        with self._lock:
            allowed = tuple(self._accepted_secrets)
        valid = False
        for secret in allowed:
            key = base64.b64decode(secret.removeprefix("whsec_"), validate=True)
            expected = "v1," + base64.b64encode(
                hmac.new(key, signed_content, hashlib.sha256).digest()
            ).decode("ascii")
            valid = valid or any(
                hmac.compare_digest(candidate, expected) for candidate in signatures
            )
        with self._lock:
            duplicate = event_id in self._seen_ids
            if valid:
                self._seen_ids.add(event_id)
        return valid, duplicate


@pytest.fixture
def https_webhook_receiver(monkeypatch, controlled_origin_certificate):
    certificate, private_key = controlled_origin_certificate
    receiver = ControlledWebhookReceiver(str(certificate), str(private_key))
    original_getaddrinfo = socket.getaddrinfo
    original_create_connection = socket.create_connection

    def getaddrinfo(host, port, *args, **kwargs):
        if host == TEST_WEBHOOK_HOST:
            return [
                (
                    socket.AF_INET,
                    socket.SOCK_STREAM,
                    socket.IPPROTO_TCP,
                    "",
                    (TEST_SOURCE_IP, port),
                )
            ]
        return original_getaddrinfo(host, port, *args, **kwargs)

    def create_connection(
        address,
        timeout=socket._GLOBAL_DEFAULT_TIMEOUT,
        source_address=None,
    ):
        host, port = address[:2]
        if host == TEST_SOURCE_IP:
            host = "127.0.0.1"
        return original_create_connection((host, port), timeout, source_address)

    monkeypatch.setattr(socket, "getaddrinfo", getaddrinfo)
    monkeypatch.setattr(socket, "create_connection", create_connection)
    try:
        yield receiver
    finally:
        receiver.close()


def _provision(client, receiver: ControlledWebhookReceiver) -> tuple[dict, str, str]:
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
