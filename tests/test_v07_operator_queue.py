"""V-07 observes retained jobs across accounts without content or mutations."""

from datetime import datetime, timedelta, timezone
from uuid import UUID, uuid4

import pytest
from sqlalchemy import event, select
from sqlalchemy.orm import Session

from app.jobs.models import TranscriptionJob


@pytest.fixture
def operator(client, monkeypatch):
    assert (
        client.post(
            "/v1/operator-sessions", json={"secret": client.app.state.operator_secret}
        ).status_code
        == 204
    )
    now = datetime.now(timezone.utc)
    monkeypatch.setattr("app.jobs.executor._utc_now", lambda: now)
    client.app.state.test_queue_now = now
    return client


def _owner(client, name="Queue account"):
    metadata, key = client.app.state.credential_store.provision_account(name)
    return metadata, key


def _seed(client, owner, state="queued", **overrides):
    now = client.app.state.test_queue_now
    values = {
        "id": f"trn_{uuid4().hex}",
        "account_id": UUID(owner["accountId"]),
        "credential_id": UUID(owner["credentialId"]),
        "status": state,
        "accepted_at": now - timedelta(minutes=5),
        "created_at": now - timedelta(minutes=6),
        "updated_at": now - timedelta(minutes=1),
        "terminal_at": now - timedelta(minutes=1)
        if state in ("completed", "failed")
        else None,
        "source_url_encrypted": b"private encrypted source URL",
        "media_object_key": "media/private-audio.wav",
        "result_object_key": "results/private-transcript.md",
        "failure_code": "PRIVATE_FAILURE_DETAIL",
        "lease_owner": "private-worker-lease",
        "lease_expires_at": now + timedelta(minutes=5),
        "download_attempts": 2,
        "client_reference": "opaque-ref",
    }
    values.update(overrides)
    with Session(client.app.state.database_engine) as session, session.begin():
        session.add(TranscriptionJob(**values))
    return values["id"]


def _iso(value):
    return value.isoformat().replace("+00:00", "Z")


def test_v07_operator_queue_empty_and_terminal_only(operator):
    assert operator.get("/v1/queue").json() == {
        "size": 0,
        "oldestAcceptedAt": None,
    }
    assert operator.get("/v1/jobs").json() == {
        "data": [],
        "pagination": {"page": 1, "size": 10, "total": 0, "totalPages": 0},
    }
    owner, _ = _owner(operator)
    for state in ("completed", "failed"):
        _seed(operator, owner, state)
    assert operator.get("/v1/queue").json() == {
        "size": 0,
        "oldestAcceptedAt": None,
    }
    assert len(operator.get("/v1/jobs").json()["data"]) == 2


def test_v07_operator_queue_counts_all_accounts_and_nonterminal_states(operator):
    first, _ = _owner(operator, "First account")
    second, _ = _owner(operator, "Second account")
    oldest = operator.app.state.test_queue_now - timedelta(days=4)
    _seed(operator, first, "downloading", accepted_at=oldest)
    _seed(operator, second, "queued")
    _seed(operator, second, "processing")
    _seed(operator, first, "completed")
    _seed(operator, second, "failed")

    response = operator.get("/v1/queue")

    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert response.json() == {"size": 3, "oldestAcceptedAt": _iso(oldest)}
    jobs = operator.get("/v1/jobs").json()["data"]
    assert len(jobs) == 5
    assert {job["accountName"] for job in jobs} == {"First account", "Second account"}
    assert {job["state"] for job in jobs} == {
        "downloading",
        "queued",
        "processing",
        "completed",
        "failed",
    }


@pytest.mark.parametrize("state", ["completed", "failed"])
def test_v07_operator_queue_retention_boundary_and_missing_job(operator, state):
    owner, _ = _owner(operator)
    cutoff = operator.app.state.test_queue_now - timedelta(hours=24)
    kept = _seed(operator, owner, state, terminal_at=cutoff + timedelta(microseconds=1))
    boundary = _seed(operator, owner, state, terminal_at=cutoff)
    expired = _seed(operator, owner, state, terminal_at=cutoff - timedelta(seconds=1))

    assert [job["jobId"] for job in operator.get("/v1/jobs").json()["data"]] == [kept]
    assert operator.get(f"/v1/jobs/{kept}").status_code == 200
    for job_id in (boundary, expired, "trn_missing"):
        response = operator.get(f"/v1/jobs/{job_id}")
        assert response.status_code == 404
        assert response.headers["content-type"] == "application/problem+json"
        assert response.json()["code"] == "NOT_FOUND"


def test_v07_operator_queue_pagination_filters_and_stable_order(operator):
    first, _ = _owner(operator)
    second, _ = _owner(operator)
    ids = sorted(_seed(operator, first) for _ in range(12))
    other = _seed(operator, second, "processing")

    pages = [
        operator.get(f"/v1/jobs?_page={page}&_size=5").json() for page in (1, 2, 3)
    ]

    assert [job["jobId"] for page in pages for job in page["data"]] == sorted(
        ids + [other]
    )
    assert pages[1]["pagination"] == {
        "page": 2,
        "size": 5,
        "total": 13,
        "totalPages": 3,
    }
    assert operator.get("/v1/jobs?_size=5").json() == pages[0]
    assert operator.get("/v1/jobs?_page=20").json()["data"] == []
    assert len(operator.get("/v1/jobs?_size=100").json()["data"]) == 13
    filtered = operator.get(
        f"/v1/jobs?accountId={second['accountId']}&state=processing"
    ).json()
    assert [job["jobId"] for job in filtered["data"]] == [other]
    assert filtered["pagination"]["total"] == 1
    assert (
        operator.get(f"/v1/jobs?accountId={first['accountId']}&state=failed").json()[
            "data"
        ]
        == []
    )
    assert operator.get(f"/v1/jobs?accountId={uuid4()}").json()["data"] == []


@pytest.mark.parametrize(
    "query",
    ["_page=0", "_size=0", "_size=101", "_page=text", "accountId=bad", "state=unknown"],
)
def test_v07_operator_queue_invalid_filters_are_safe(operator, query):
    response = operator.get(f"/v1/jobs?{query}")
    assert response.status_code == 400
    assert response.json()["code"] == "INVALID_REQUEST"


def test_v07_operator_queue_allowlist_no_content_access_and_no_job_mutations(
    operator, monkeypatch
):
    owner, key = _owner(operator, "<script>unsafe account name</script>")
    reference = "<img src=x onerror=alert(1)>"
    job_id = _seed(operator, owner, "completed", client_reference=reference)
    engine = operator.app.state.database_engine
    with engine.connect() as connection:
        before = connection.execute(select(TranscriptionJob.__table__)).mappings().all()

    def forbidden(*args, **kwargs):
        pytest.fail("Operational reads must not access content or worker methods")

    jobs = operator.app.state.jobs
    monkeypatch.setattr(jobs, "source_url_for_recovery", forbidden)
    monkeypatch.setattr(jobs, "get", forbidden)
    monkeypatch.setattr(jobs, "get_result_reference", forbidden)
    monkeypatch.setattr(jobs._source_url_cipher, "decrypt", forbidden)
    monkeypatch.setattr(jobs._source_connector, "start", forbidden)
    monkeypatch.setattr(operator.app.state.object_store, "get_bytes", forbidden)
    monkeypatch.setattr(operator.app.state.object_store, "download_media", forbidden)
    statements = []

    def record_sql(connection, cursor, statement, parameters, context, executemany):
        statements.append(statement)

    event.listen(engine, "before_cursor_execute", record_sql)
    try:
        summary = operator.get("/v1/queue")
        listing = operator.get("/v1/jobs")
        detail = operator.get(f"/v1/jobs/{job_id}")
    finally:
        event.remove(engine, "before_cursor_execute", record_sql)

    expected = {
        "jobId": job_id,
        "accountId": owner["accountId"],
        "accountName": "<script>unsafe account name</script>",
        "credentialId": owner["credentialId"],
        "state": "completed",
        "acceptedAt": _iso(operator.app.state.test_queue_now - timedelta(minutes=5)),
        "terminalAt": _iso(operator.app.state.test_queue_now - timedelta(minutes=1)),
        "clientReference": reference,
    }
    assert detail.json() == expected
    assert listing.json()["data"] == [expected]
    for response in (summary, listing, detail):
        assert response.status_code == 200
        assert response.headers["cache-control"] == "no-store"
        for secret in (
            key,
            operator.app.state.operator_secret,
            "private-audio",
            "private-transcript",
            "private-worker",
            "PRIVATE_FAILURE_DETAIL",
            "source_url",
            "result_object",
            "media_object",
        ):
            assert secret not in response.text
    assert statements
    for statement in statements:
        assert statement.lstrip().upper().startswith("SELECT")
        assert "FOR UPDATE" not in statement.upper()
        for column in (
            "source_url_encrypted",
            "media_object_key",
            "result_object_key",
            "lease_owner",
            "failure_code",
        ):
            assert column not in statement
    with engine.connect() as connection:
        after = connection.execute(select(TranscriptionJob.__table__)).mappings().all()
    assert after == before


def test_v07_operator_queue_optional_fields_and_unaccepted_reservation(operator):
    owner, _ = _owner(operator)
    job_id = _seed(operator, owner, client_reference=None)
    _seed(operator, owner, "downloading", accepted_at=None)
    detail = operator.get(f"/v1/jobs/{job_id}").json()
    assert "terminalAt" not in detail
    assert "clientReference" not in detail
    assert operator.get("/v1/queue").json()["size"] == 1
    assert operator.get("/v1/jobs").json()["pagination"]["total"] == 1


def test_v07_operator_queue_customer_remains_scoped_to_credential(operator):
    first, first_key = _owner(operator, "First")
    second, second_key = _owner(operator, "Second")
    same_account, same_account_key = (
        operator.app.state.credential_store.issue_for_account(UUID(first["accountId"]))
    )
    first_job = _seed(operator, first)
    second_job = _seed(operator, second)
    sibling_job = _seed(operator, {**same_account, "accountId": first["accountId"]})

    for key, own_job in (
        (first_key, first_job),
        (second_key, second_job),
        (same_account_key, sibling_job),
    ):
        for job_id in (first_job, second_job, sibling_job):
            response = operator.get(
                f"/v1/transcriptions/{job_id}", headers={"X-API-Key": key}
            )
            assert response.status_code == (200 if job_id == own_job else 404)
    assert operator.get(f"/v1/transcriptions/{first_job}").status_code == 401
    assert operator.get("/v1/jobs").json()["pagination"]["total"] == 3
    operator.delete("/v1/operator-sessions")
    for path in ("/v1/queue", "/v1/jobs", f"/v1/jobs/{first_job}"):
        response = operator.get(path, headers={"X-API-Key": first_key})
        assert response.status_code == 401


def test_v07_operator_queue_console_summary_age_pagination_and_detail(operator):
    page = operator.get("/operator").text
    for element in (
        "queue-summary",
        "queue-refresh",
        "queue-error",
        "job-list",
        "jobs-previous",
        "jobs-next",
        "jobs-pagination",
        "job-detail",
    ):
        assert f'id="{element}"' in page
    assert 'queueRequest("/v1/queue")' in page
    assert "queueRequest(`/v1/jobs?_page=${page}&_size=10`)" in page
    assert "queueRequest(`/v1/jobs/${encodeURIComponent(jobId)}`)" in page
    assert "Date.parse(oldest)" in page
    assert "window.setInterval(renderQueueSummary, 1000)" in page
    assert "item.textContent = `${job.accountName}" in page
    assert "innerHTML" not in page
    operations = operator.get("/openapi.json").json()["paths"]
    for path, operation in (
        ("/v1/queue", "getQueue"),
        ("/v1/jobs", "listOperationalJobs"),
        ("/v1/jobs/{jobId}", "getOperationalJob"),
    ):
        assert operations[path]["get"]["operationId"] == operation
    operator.delete("/v1/operator-sessions")
    assert 'id="job-list"' not in operator.get("/operator").text
