"""V-03 acquisition, S3 storage, processing, limits, and recovery tests."""

from __future__ import annotations

import io
import json
import time
import wave
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from sqlalchemy import func, select, update

from app.jobs.models import TranscriptionJob
from app.jobs.security import SourceURLCipher
from app.jobs.worker import JobWorker
from app.transcription.models import Segment
from app.transcription.source import SourceConnector
from app.transcription.validation import MediaDurationExceeded


class RecordingTranscriber:
    def __init__(self, segments: list[Segment] | None = None) -> None:
        self.segments = segments or []
        self.calls = 0

    def transcribe(self, media_path):
        self.calls += 1
        return self.segments, 1.0


def _wav_bytes(seconds: int = 1) -> bytes:
    destination = io.BytesIO()
    with wave.open(destination, "wb") as audio:
        audio.setnchannels(1)
        audio.setsampwidth(2)
        audio.setframerate(16000)
        audio.writeframes(b"\x00\x00" * 16000 * seconds)
    return destination.getvalue()


def _create(client, source_url: str, key: str | None = None) -> dict:
    _, api_key = client.app.state.credential_store.provision_account(
        f"V-03 {uuid4().hex}"
    )
    response = client.post(
        "/v1/transcriptions",
        headers={
            "X-API-Key": api_key,
            "Idempotency-Key": key or uuid4().hex,
        },
        json={"sourceUrl": source_url},
    )
    assert response.status_code == 202, response.text
    return {"job": response.json(), "api_key": api_key}


def _worker(client, transcriber=None, *, sleeper=None) -> JobWorker:
    return JobWorker(
        client.app.state.database_engine,
        SourceURLCipher.from_hex("1a" * 32),
        SourceConnector(),
        client.app.state.object_store,
        transcriber or RecordingTranscriber(),
        client.app.state.job_metrics,
        sleeper=sleeper or (lambda seconds: None),
        jitter=lambda minimum, _maximum: minimum,
    )


def _status(client, job_id: str, api_key: str) -> dict:
    response = client.get(
        f"/v1/transcriptions/{job_id}", headers={"X-API-Key": api_key}
    )
    assert response.status_code == 200
    return response.json()


def _assert_failed(client, job_id: str, api_key: str, code: str) -> dict:
    state = _status(client, job_id, api_key)
    assert state["status"] == "failed"
    assert state["failure"]["code"] == code
    assert state["terminalAt"].endswith("Z")
    return state


def _job_row(client, job_id: str) -> dict:
    with client.app.state.database_engine.connect() as connection:
        return (
            connection.execute(
                select(
                    TranscriptionJob.status,
                    TranscriptionJob.source_url_encrypted,
                    TranscriptionJob.media_object_key,
                    TranscriptionJob.result_object_key,
                    TranscriptionJob.download_attempts,
                    TranscriptionJob.download_not_before,
                ).where(TranscriptionJob.id == job_id)
            )
            .mappings()
            .one()
        )


def test_v03_processing_downloads_to_private_s3_and_discards_source_url(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = _create(client, origin.url_for("/sample.wav"))
    worker = _worker(client)

    assert worker.run_download_once() is True

    state = _status(client, created["job"]["jobId"], created["api_key"])
    row = _job_row(client, state["jobId"])
    assert state["status"] == "queued"
    assert state["createdAt"] and state["updatedAt"]
    assert row["source_url_encrypted"] is None
    assert row["status"] == "queued"
    assert row["media_object_key"].startswith(f"media/{state['jobId']}/")
    assert origin.source_url not in json.dumps(state)
    stored = client.app.state.object_store.get_bytes(row["media_object_key"])
    assert stored == origin.body
    assert (
        client.app.state.download_metrics.snapshot()["downloadStartSecondsP95"] >= 0
    )


def test_v03_processing_retries_transient_http_and_respects_retry_after(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    origin.status_codes = [200, 429, 200]
    origin.retry_after_values = [None, "2", None]
    created = _create(client, origin.url_for("/retry.wav"))
    delays: list[float] = []
    worker = _worker(client, sleeper=delays.append)

    worker.run_download_once()

    assert origin.count_requests() == 3
    assert delays == [2.0]
    assert (
        _status(client, created["job"]["jobId"], created["api_key"])["status"]
        == "queued"
    )
    assert _job_row(client, created["job"]["jobId"])["download_attempts"] == 3


def test_v03_processing_permanent_and_exhausted_source_failures_are_terminal(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    origin.status_codes = [200, 404, 200, 503, 503]
    created = _create(client, origin.url_for("/permanent.wav"))
    worker = _worker(client)

    worker.run_download_once()
    _assert_failed(
        client, created["job"]["jobId"], created["api_key"], "SOURCE_UNAVAILABLE"
    )
    assert _job_row(client, created["job"]["jobId"])["download_attempts"] == 2

    retrying = _create(client, origin.url_for("/retry-limit.wav"))
    worker.run_download_once()
    _assert_failed(
        client, retrying["job"]["jobId"], retrying["api_key"], "SOURCE_UNAVAILABLE"
    )
    assert _job_row(client, retrying["job"]["jobId"])["download_attempts"] == 3
    assert origin.count_requests() == 5


def test_v03_processing_does_not_start_a_request_after_source_expiry(
    client, monkeypatch
):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    issued_at = datetime.now(timezone.utc).replace(microsecond=0)
    expires_at = issued_at + timedelta(hours=1)
    valid_source = origin.url_for(
        "/expires-during-queue.wav",
        f"X-Amz-Date={issued_at.strftime('%Y%m%dT%H%M%SZ')}&X-Amz-Expires=3600",
    )
    created = _create(client, valid_source)
    monkeypatch.setattr(
        "app.jobs.worker._utc_now",
        lambda: expires_at + timedelta(minutes=1),
    )

    _worker(client).run_download_once()

    _assert_failed(
        client, created["job"]["jobId"], created["api_key"], "SOURCE_UNAVAILABLE"
    )
    assert origin.count_requests() == 1
    assert _job_row(client, created["job"]["jobId"])["download_attempts"] == 1

    _, api_key = client.app.state.credential_store.provision_account(
        f"V-03 expired {uuid4().hex}"
    )
    expired_source = origin.url_for(
        "/already-expired.wav", "X-Amz-Date=20200101T000000Z&X-Amz-Expires=60"
    )
    response = client.post(
        "/v1/transcriptions",
        headers={
            "X-API-Key": api_key,
            "Idempotency-Key": uuid4().hex,
        },
        json={"sourceUrl": expired_source},
    )
    assert response.status_code == 422
    body = response.json()
    assert body["code"] == "INVALID_SOURCE_URL"
    assert expired_source not in response.text
    assert origin.count_requests() == 1


def test_v03_processing_enforces_streaming_size_limit(client, monkeypatch):
    origin = client.app.state.test_source
    origin.body = b"m" * 64
    origin.omit_content_length = True
    created = _create(client, origin.url_for("/chunked.wav"))
    monkeypatch.setattr("app.transcription.source.MAX_MEDIA_SIZE_BYTES", 32)

    _worker(client).run_download_once()

    _assert_failed(
        client,
        created["job"]["jobId"],
        created["api_key"],
        "MEDIA_SIZE_LIMIT_EXCEEDED",
    )
    assert _job_row(client, created["job"]["jobId"])["media_object_key"] is None


def test_v03_processing_rejects_unrecognized_container_and_codec(client):
    origin = client.app.state.test_source
    origin.body = b"not a valid MP3 or audio container"
    created = _create(client, origin.url_for("/invalid.mp3"))

    _worker(client).run_download_once()

    _assert_failed(
        client,
        created["job"]["jobId"],
        created["api_key"],
        "UNSUPPORTED_MEDIA_FORMAT",
    )
    assert client.app.state.job_metrics.snapshot()["failures"] == {
        "download.UNSUPPORTED_MEDIA_FORMAT": 1
    }


def test_v03_processing_rejects_media_longer_than_two_hours(client, monkeypatch):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = _create(client, origin.url_for("/too-long.wav"))

    def reject_duration(_path):
        raise MediaDurationExceeded

    monkeypatch.setattr("app.jobs.worker.inspect_media", reject_duration)
    _worker(client).run_download_once()

    _assert_failed(
        client,
        created["job"]["jobId"],
        created["api_key"],
        "MEDIA_DURATION_LIMIT_EXCEEDED",
    )


def test_v03_processing_serial_worker_publishes_versioned_result_and_metrics(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = _create(client, origin.url_for("/complete.wav"))
    transcriber = RecordingTranscriber([Segment(0.125, 0.5, " Olá")])
    worker = _worker(client, transcriber)

    worker.run_download_once()
    assert (
        _status(client, created["job"]["jobId"], created["api_key"])["status"]
        == "queued"
    )
    worker.run_processing_once()

    state = _status(client, created["job"]["jobId"], created["api_key"])
    row = _job_row(client, state["jobId"])
    payload = json.loads(
        client.app.state.object_store.get_bytes(row["result_object_key"])
    )
    assert state["status"] == "completed"
    assert state["terminalAt"].endswith("Z")
    assert payload == {
        "schemaVersion": 1,
        "jobId": state["jobId"],
        "language": "pt-BR",
        "durationMs": 1000,
        "segments": [{"startMs": 125, "endMs": 500, "text": " Olá"}],
    }
    assert transcriber.calls == 1
    metrics = client.app.state.job_metrics.snapshot()
    assert metrics["counts"]["downloadsQueued"] == 1
    assert metrics["counts"]["processingCompleted"] == 1
    assert metrics["durations"]["download"]["secondsP95"] >= 0
    assert metrics["durations"]["queueWait"]["count"] == 1
    assert metrics["durations"]["queueWait"]["secondsP95"] >= 0
    assert metrics["durations"]["processing"]["secondsP95"] >= 0
    assert metrics["durations"]["acceptedToTerminal"]["count"] == 1
    assert metrics["durations"]["acceptedToTerminal"]["secondsP95"] >= 0
    assert metrics["realtimeFactorCount"] == 1
    assert metrics["resources"]["sampledTranscriptions"] == 1
    assert row["media_object_key"] is None


def test_v03_processing_recovers_expired_leases_without_duplicate_transcription(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = _create(client, origin.url_for("/recover.wav"))
    job_id = created["job"]["jobId"]
    engine = client.app.state.database_engine
    with engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(
                lease_owner="interrupted-download",
                lease_expires_at=datetime.now(timezone.utc) - timedelta(seconds=1),
            )
        )

    transcriber = RecordingTranscriber()
    restarted_worker = _worker(client, transcriber)
    assert restarted_worker.run_download_once() is True
    with engine.begin() as connection:
        connection.execute(
            update(TranscriptionJob)
            .where(TranscriptionJob.id == job_id)
            .values(
                status="processing",
                lease_owner="interrupted-processing",
                lease_expires_at=datetime.now(timezone.utc) - timedelta(seconds=1),
            )
        )

    assert restarted_worker.run_processing_once() is True
    assert restarted_worker.run_processing_once() is False
    assert transcriber.calls == 1
    assert _status(client, job_id, created["api_key"])["status"] == "completed"


def test_v03_processing_limits_global_download_concurrency_to_two(client):
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = [
        _create(client, origin.url_for(f"/parallel-{index}.wav")) for index in range(3)
    ]
    origin.active_requests = 0
    origin.max_concurrent_requests = 0
    origin.response_delay_seconds = 0.15
    workers = [_worker(client), _worker(client)]

    with ThreadPoolExecutor(max_workers=2) as executor:
        results = list(executor.map(lambda worker: worker.run_download_once(), workers))

    statuses = [
        _status(client, item["job"]["jobId"], item["api_key"])["status"]
        for item in created
    ]
    assert results == [True, True]
    assert statuses.count("queued") == 2
    assert statuses.count("downloading") == 1
    assert origin.max_concurrent_requests == 2
    with client.app.state.database_engine.connect() as connection:
        active_leases = connection.scalar(
            select(func.count())
            .select_from(TranscriptionJob)
            .where(
                TranscriptionJob.status == "downloading",
                TranscriptionJob.lease_expires_at.is_not(None),
            )
        )
    assert active_leases == 0


def test_v03_processing_transient_failure_at_admission_is_accepted_for_retry(client):
    """Q-04: a transient origin status at admission returns 202, not rejection.

    The admission GET counts as the first of the three permitted origin
    attempts; the downloader owns the remaining retries with jitter. The
    Retry-After received at admission is preserved as the earliest instant
    for worker attempt 2, so the retry never starts before it is due.
    """
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    origin.status_codes = [503, 200]
    origin.retry_after_values = ["0.4", None]
    # Deterministic admission jitter: delay == max(Retry-After, 0) == 0.4 s.
    client.app.state.jobs._jitter = lambda _minimum, _maximum: 0.0
    created = _create(client, origin.url_for("/transient-admission.wav"))

    row = _job_row(client, created["job"]["jobId"])
    assert created["job"]["status"] == "downloading"
    assert row["download_attempts"] == 1
    assert row["source_url_encrypted"] is not None
    assert row["download_not_before"] is not None
    assert origin.count_requests() == 1

    # The retry must not start before the scheduled instant.
    assert _worker(client).run_download_once() is False
    assert (
        _status(client, created["job"]["jobId"], created["api_key"])["status"]
        == "downloading"
    )
    assert _job_row(client, created["job"]["jobId"])["download_attempts"] == 1
    assert origin.count_requests() == 1

    time.sleep(0.5)

    _worker(client).run_download_once()

    assert (
        _status(client, created["job"]["jobId"], created["api_key"])["status"]
        == "queued"
    )
    assert _job_row(client, created["job"]["jobId"])["download_attempts"] == 2
    assert origin.count_requests() == 2


def test_v03_processing_permanent_origin_at_admission_returns_422(client):
    """A permanent origin rejection known at admission uses 422, not 500.

    No job is persisted and the worker has nothing to repeat outside the
    retry policy.
    """
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    origin.status_codes = [404]
    _, api_key = client.app.state.credential_store.provision_account(
        f"V-03 permanent-admission {uuid4().hex}"
    )
    source_url = origin.url_for("/missing-at-admission.wav")
    response = client.post(
        "/v1/transcriptions",
        headers={"X-API-Key": api_key, "Idempotency-Key": uuid4().hex},
        json={"sourceUrl": source_url},
    )

    assert response.status_code == 422
    assert response.json()["code"] == "INVALID_SOURCE_URL"
    assert source_url not in response.text
    assert origin.count_requests() == 1
    with client.app.state.database_engine.connect() as connection:
        job_count = connection.scalar(
            select(func.count()).select_from(TranscriptionJob)
        )
    assert job_count == 0


def test_v03_processing_real_api_worker_composition_reaches_terminal(
    all_roles_client,
):
    """The composed application (API + worker roles) runs the full cycle.

    Evidence that the real role composition boots: the lifespan starts the
    background downloader/transcription threads next to the HTTP API and a
    job created over HTTP reaches a terminal state through them.
    """
    client = all_roles_client
    assert client.app.state.settings.ROLE == "all"
    assert client.app.state.worker is not None
    assert all(thread.is_alive() for thread in client.app.state.worker._threads)

    health = client.get("/health")
    assert health.status_code == 200
    assert health.json()["role"] == "all"
    assert health.json()["modelLoaded"] is True

    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    created = _create(client, origin.url_for("/composed.wav"))

    state = _status(client, created["job"]["jobId"], created["api_key"])
    deadline = time.monotonic() + 30.0
    while state["status"] not in {"completed", "failed"} and time.monotonic() < deadline:
        time.sleep(0.2)
        state = _status(client, created["job"]["jobId"], created["api_key"])

    assert state["status"] == "completed"
    assert state["terminalAt"].endswith("Z")
    assert origin.source_url not in json.dumps(state)


def test_v03_processing_local_load_measures_p95_for_ten_jobs(client):
    """Local load evidence: 10 disposable-WAV jobs with real measured p95s.

    Environment limits: controlled HTTPS origin and Moto S3 on loopback,
    FakeTranscriber-free RecordingTranscriber (no model inference), and
    small disposable WAV media — not representative pilot media. The pilot
    capacity evaluation with representative media stays an external
    prerequisite. Targets: p95 <= 30 s to connection start and
    p95 <= 24 h acceptedAt-to-terminal for up to 10 jobs/day.
    """
    origin = client.app.state.test_source
    origin.body = _wav_bytes()
    transcriber = RecordingTranscriber([Segment(0.0, 1.0, " carga")])

    created = [ _create(client, origin.url_for(f"/load-{index}.wav")) for index in range(10) ]
    worker = _worker(client, transcriber)
    for _ in range(10):
        assert worker.run_download_once() is True
    for _ in range(10):
        assert worker.run_processing_once() is True

    for item in created:
        state = _status(client, item["job"]["jobId"], item["api_key"])
        assert state["status"] == "completed"
    assert transcriber.calls == 10

    download_snapshot = client.app.state.download_metrics.snapshot()
    assert download_snapshot["downloadStartCount"] == 10
    connection_p95 = download_snapshot["downloadStartSecondsP95"]
    assert 0 <= connection_p95 <= 30.0

    job_snapshot = client.app.state.job_metrics.snapshot()
    accepted_to_terminal = job_snapshot["durations"]["acceptedToTerminal"]
    assert accepted_to_terminal["count"] == 10
    terminal_p95 = accepted_to_terminal["secondsP95"]
    assert 0 <= terminal_p95 <= 24 * 3600
    print(
        f"\n[V-03 load] jobs=10 connectionStartP95={connection_p95:.4f}s "
        f"acceptedToTerminalP95={terminal_p95:.4f}s "
        f"realtimeFactorAvg={job_snapshot['realtimeFactorAverage']}"
    )
