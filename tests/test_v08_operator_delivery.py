"""V-08 observes delivery state without content access or scheduling mutations."""

from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, select, update
from sqlalchemy.orm import Session

from app.jobs.models import NotificationEvent, TranscriptionJob
from app.jobs.webhooks import persist_terminal_event
from app.jobs.worker import JobWorker


@pytest.fixture
def operator(client):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    return client


def _owner(client, receiver):
    metadata, key = client.app.state.credential_store.provision_account(
        "Delivery owner"
    )
    secret = client.app.state.webhook_config_store.configure(
        UUID(metadata["accountId"]), receiver.url
    )
    receiver.allow_secret(secret)
    return metadata, key, secret


def _event(client, owner, state="completed"):
    now = datetime.now(timezone.utc)
    job_id = f"trn_{uuid4().hex}"
    with Session(client.app.state.database_engine) as session, session.begin():
        job = TranscriptionJob(
            id=job_id,
            account_id=UUID(owner["accountId"]),
            credential_id=UUID(owner["credentialId"]),
            status=state,
            accepted_at=now,
            terminal_at=now,
            source_url_encrypted=b"private encrypted source",
            client_reference="private-payload-reference",
        )
        session.add(job)
        session.flush()
        event_id = persist_terminal_event(session, job, now).event_id
    return job_id, event_id


def _snapshot(client, model):
    with client.app.state.database_engine.connect() as connection:
        return connection.execute(select(model.__table__)).mappings().all()


@pytest.mark.parametrize("state", ["completed", "failed"])
@pytest.mark.parametrize("http_status", [503, 400])
def test_v08_operator_delivery_failure_and_retry_preserve_jobs_and_schedule(
    operator, https_webhook_receiver, state, http_status
):
    owner, _, _ = _owner(operator, https_webhook_receiver)
    job_id, event_id = _event(operator, owner, state)
    jobs_before = _snapshot(operator, TranscriptionJob)
    https_webhook_receiver.respond_with(http_status, retry_after=("60",))
    assert operator.app.state.notification_dispatcher.run_once() is True
    events_before = _snapshot(operator, NotificationEvent)

    response = operator.get(f"/v1/delivery-attempts?jobId={job_id}")

    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    attempt = response.json()["data"][0]
    assert UUID(attempt["attemptId"]).version == 5
    assert attempt["accountId"] == owner["accountId"]
    assert attempt["jobId"] == job_id
    assert attempt["eventId"] == event_id
    assert attempt["willRetry"] is (http_status == 503)
    assert attempt["destinationActive"] is True
    assert str(http_status) in attempt["failure"]
    assert attempt["occurredAt"].endswith("Z")
    assert _snapshot(operator, TranscriptionJob) == jobs_before
    assert _snapshot(operator, NotificationEvent) == events_before
    assert operator.get(f"/v1/jobs/{job_id}").json()["state"] == state
    assert (
        operator.get(f"/v1/delivery-attempts?jobId={job_id}").json() == response.json()
    )


def test_v08_operator_delivery_survives_physical_job_retention(
    operator, https_webhook_receiver, monkeypatch
):
    owner, _, _ = _owner(operator, https_webhook_receiver)
    job_id, event_id = _event(operator, owner)
    https_webhook_receiver.respond_with(503, retry_after=("60",))
    assert operator.app.state.notification_dispatcher.run_once()
    before = operator.get(f"/v1/delivery-attempts?jobId={job_id}").json()
    now = datetime.now(timezone.utc)
    with Session(operator.app.state.database_engine) as session, session.begin():
        session.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(terminal_at=now - timedelta(hours=25))
        )
    monkeypatch.setattr("app.jobs.worker._utc_now", lambda: now)
    worker = JobWorker(
        operator.app.state.database_engine,
        operator.app.state.source_url_cipher,
        operator.app.state.source_connector,
        operator.app.state.object_store,
        None,
        operator.app.state.job_metrics,
    )
    assert worker.run_retention_once()
    assert _snapshot(operator, TranscriptionJob) == []
    assert operator.get(f"/v1/jobs/{job_id}").status_code == 404
    assert operator.get(f"/v1/delivery-attempts?jobId={job_id}").json() == before
    assert before["data"][0]["eventId"] == event_id


@pytest.mark.parametrize("by_receiver", [True, False])
def test_v08_operator_delivery_inactive_destination_in_account_context(
    operator, https_webhook_receiver, by_receiver
):
    owner, _, _ = _owner(operator, https_webhook_receiver)
    job_id, _ = _event(operator, owner)
    https_webhook_receiver.respond_with(
        410 if by_receiver else 503, retry_after=("60",)
    )
    assert operator.app.state.notification_dispatcher.run_once()
    if not by_receiver:
        assert (
            operator.post(
                f"/v1/accounts/{owner['accountId']}/webhook-destination/deactivate",
                json={"reason": "Contain delivery"},
            ).status_code
            == 200
        )
    account = operator.get(f"/v1/accounts/{owner['accountId']}")
    destination = operator.get(f"/v1/accounts/{owner['accountId']}/webhook-destination")
    assert account.status_code == 200
    assert account.json()["webhookDestination"]["active"] is False
    assert destination.json()["active"] is False
    attempts = operator.get(f"/v1/delivery-attempts?accountId={owner['accountId']}")
    attempt = attempts.json()["data"][0]
    assert attempt["jobId"] == job_id
    assert attempt["destinationActive"] is False
    assert attempt["willRetry"] is False
    assert "desativado" in attempt["failure"]


def _seed_attempts(client, owner, count, **overrides):
    now = datetime.now(timezone.utc)
    ids = []
    with Session(client.app.state.database_engine) as session, session.begin():
        for _ in range(count):
            event_id = f"evt_{uuid4().hex}"
            values = {
                "event_id": event_id,
                "job_id": f"trn_{uuid4().hex}",
                "account_id": UUID(owner["accountId"]),
                "payload": b"private-payload-with-source-url-and-material",
                "status": "pending",
                "attempt_count": 1,
                "last_attempt_at": now,
                "next_attempt_at": now + timedelta(minutes=5),
                "retry_deadline": now + timedelta(hours=48),
                "last_status_code": 503,
                "last_error_code": "transient_delivery_failure",
            }
            values.update(overrides)
            session.add(NotificationEvent(**values))
            ids.append(event_id)
    return ids


def test_v08_operator_delivery_pagination_filters_order_and_unattempted_events(
    operator, https_webhook_receiver
):
    first, _, _ = _owner(operator, https_webhook_receiver)
    second, _, _ = _owner(operator, https_webhook_receiver)
    ids = sorted(_seed_attempts(operator, first, 12))
    closed = _seed_attempts(operator, second, 1, status="failed", next_attempt_at=None)[
        0
    ]
    _event(operator, first)  # A queued event is not a delivery attempt yet.
    pages = [
        operator.get(
            f"/v1/delivery-attempts?accountId={first['accountId']}&_page={page}&_size=5"
        ).json()
        for page in (1, 2, 3)
    ]
    assert [row["eventId"] for page in pages for row in page["data"]] == ids
    assert pages[1]["pagination"] == {
        "page": 2,
        "size": 5,
        "total": 12,
        "totalPages": 3,
    }
    assert operator.get("/v1/delivery-attempts?_page=50").json()["data"] == []
    assert (
        operator.get("/v1/delivery-attempts?_size=100").json()["pagination"]["total"]
        == 13
    )
    assert (
        len(operator.get("/v1/delivery-attempts?willRetry=true").json()["data"]) == 10
    )
    assert (
        operator.get("/v1/delivery-attempts?willRetry=true").json()["pagination"][
            "total"
        ]
        == 12
    )
    assert [
        row["eventId"]
        for row in operator.get("/v1/delivery-attempts?willRetry=false").json()["data"]
    ] == [closed]
    job_id = pages[0]["data"][0]["jobId"]
    assert (
        operator.get(f"/v1/delivery-attempts?jobId={job_id}").json()["pagination"][
            "total"
        ]
        == 1
    )
    for query in (
        f"accountId={second['accountId']}&jobId={job_id}",
        f"accountId={first['accountId']}&willRetry=false",
        f"accountId={uuid4()}",
        "jobId=trn_missing",
    ):
        assert operator.get(f"/v1/delivery-attempts?{query}").json()["data"] == []


@pytest.mark.parametrize(
    "query", ["_page=0", "_size=101", "accountId=bad", "willRetry=bad"]
)
def test_v08_operator_delivery_rejects_invalid_filters(operator, query):
    response = operator.get(f"/v1/delivery-attempts?{query}")
    assert response.status_code == 400
    assert response.json()["code"] == "INVALID_REQUEST"


def test_v08_operator_delivery_expired_retry_is_closed_without_rescheduling(
    operator, https_webhook_receiver, monkeypatch
):
    owner, _, _ = _owner(operator, https_webhook_receiver)
    now = datetime.now(timezone.utc)
    _seed_attempts(operator, owner, 1, retry_deadline=now)
    monkeypatch.setattr("app.jobs.webhooks._utc_now", lambda: now)
    before = _snapshot(operator, NotificationEvent)
    assert operator.get("/v1/delivery-attempts?willRetry=true").json()["data"] == []
    assert (
        len(operator.get("/v1/delivery-attempts?willRetry=false").json()["data"]) == 1
    )
    assert _snapshot(operator, NotificationEvent) == before


def test_v08_operator_delivery_projection_never_loads_content_or_secrets(
    operator, https_webhook_receiver, monkeypatch
):
    owner, key, secret = _owner(operator, https_webhook_receiver)
    _seed_attempts(
        operator, owner, 1, last_error_code="https://private/media?token=secret"
    )
    engine = operator.app.state.database_engine
    before = _snapshot(operator, NotificationEvent)

    def forbidden(*args, **kwargs):
        pytest.fail("Delivery read must not access content, signing keys or dispatcher")

    monkeypatch.setattr(operator.app.state.webhook_secret_cipher, "decrypt", forbidden)
    monkeypatch.setattr(operator.app.state.source_url_cipher, "decrypt", forbidden)
    monkeypatch.setattr(operator.app.state.object_store, "get_bytes", forbidden)
    monkeypatch.setattr(
        operator.app.state.notification_dispatcher, "run_once", forbidden
    )
    statements = []

    def record_sql(connection, cursor, statement, parameters, context, executemany):
        statements.append(statement)

    event.listen(engine, "before_cursor_execute", record_sql)
    try:
        response = operator.get("/v1/delivery-attempts")
    finally:
        event.remove(engine, "before_cursor_execute", record_sql)
    assert response.status_code == 200
    assert set(response.json()["data"][0]) == {
        "attemptId",
        "accountId",
        "jobId",
        "eventId",
        "occurredAt",
        "willRetry",
        "failure",
        "destinationActive",
    }
    assert (
        response.json()["data"][0]["failure"]
        == "Falha de entrega; receptor respondeu 503"
    )
    for private in (
        key,
        secret,
        "private",
        "payload",
        "https://",
        "verificationMaterial",
    ):
        assert private not in response.text
    assert statements
    for statement in statements:
        assert statement.lstrip().upper().startswith("SELECT")
        assert "FOR UPDATE" not in statement.upper()
        for column in (
            "payload",
            "secret_ciphertext",
            "transcription_jobs",
            "webhook_secrets",
        ):
            assert column not in statement
    assert _snapshot(operator, NotificationEvent) == before


def test_v08_operator_delivery_successful_attempt_and_stable_event_across_retry(
    operator, https_webhook_receiver
):
    owner, _, _ = _owner(operator, https_webhook_receiver)
    job_id, event_id = _event(operator, owner)
    https_webhook_receiver.respond_with(503, 200)
    dispatcher = operator.app.state.notification_dispatcher
    assert dispatcher.run_once()
    first = operator.get(f"/v1/delivery-attempts?jobId={job_id}").json()["data"][0]
    with Session(operator.app.state.database_engine) as session, session.begin():
        session.execute(
            update(NotificationEvent)
            .where(NotificationEvent.event_id == event_id)
            .values(next_attempt_at=datetime.now(timezone.utc))
        )
    assert dispatcher.run_once()
    latest = operator.get(f"/v1/delivery-attempts?jobId={job_id}").json()["data"]
    assert len(latest) == 1
    assert latest[0]["eventId"] == first["eventId"] == event_id
    assert latest[0]["attemptId"] != first["attemptId"]
    assert latest[0]["willRetry"] is False
    assert "failure" not in latest[0]


def test_v08_operator_delivery_console_context_and_session_isolation(
    operator, https_webhook_receiver
):
    _, api_key, _ = _owner(operator, https_webhook_receiver)
    page = operator.get("/operator").text
    for element in (
        "account-delivery-open",
        "delivery-context",
        "delivery-filter",
        "delivery-refresh",
        "delivery-list",
        "delivery-error",
        "delivery-previous",
        "delivery-next",
    ):
        assert f'id="{element}"' in page
    assert "fetch(`/v1/delivery-attempts?${query}`" in page
    assert 'query.set("jobId", context.jobId)' in page
    assert 'query.set("willRetry", deliveryFilter.value)' in page
    assert "Ver tentativas deste job" in page
    assert "accountId: job.accountId, jobId: job.jobId" in page
    assert "Pendente de retentativa" in page
    assert "Falha encerrada" in page
    assert "Destino inativo." in page
    assert "innerHTML" not in page
    assert "/result" not in page
    operations = operator.get("/openapi.json").json()["paths"]
    assert (
        operations["/v1/delivery-attempts"]["get"]["operationId"]
        == "listDeliveryAttempts"
    )
    operator.delete("/v1/operator-sessions")
    response = operator.get("/v1/delivery-attempts", headers={"X-API-Key": api_key})
    assert response.status_code == 401
    assert 'id="delivery-list"' not in operator.get("/operator").text
