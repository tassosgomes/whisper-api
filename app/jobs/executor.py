"""Durable job acceptance and idempotency handling."""

from __future__ import annotations

import hmac
import random
import time
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from sqlalchemy import select
from sqlalchemy.exc import IntegrityError
from sqlalchemy.engine import Engine
from sqlalchemy.orm import sessionmaker

from app.jobs.models import IdempotencyRecord, TranscriptionJob
from app.jobs.security import SourceURLCipher
from app.monitoring.metrics import DownloadStartMetrics
from app.transcription.paths import InvalidSourceURL
from app.transcription.source import (
    ExpiredSourceURL,
    MediaSizeExceeded,
    SourceConnector,
    SourceUnavailable,
    TransientSourceFailure,
)


IDEMPOTENCY_WINDOW = timedelta(seconds=120)
DOWNLOAD_CONNECT_CONCURRENCY = 2
# Q-04 first retry window: the admission GET counts as attempt 1 of 3, so the
# scheduled wait before worker attempt 2 uses full jitter 0-5 s.
ADMISSION_RETRY_JITTER_SECONDS = 5.0


class IdempotencyKeyReused(ValueError):
    """The caller reused a key with a semantically different JSON payload."""


class JobManager:
    """Persist accepted jobs and serialize competing requests by idempotency key."""

    def __init__(
        self,
        engine: Engine,
        source_url_cipher: SourceURLCipher,
        source_connector: SourceConnector,
        download_metrics: DownloadStartMetrics,
        *,
        jitter=random.uniform,
    ) -> None:
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._source_url_cipher = source_url_cipher
        self._source_connector = source_connector
        self._download_metrics = download_metrics
        self._jitter = jitter
        self._download_executor = ThreadPoolExecutor(
            max_workers=DOWNLOAD_CONNECT_CONCURRENCY,
            thread_name_prefix="source-connect",
        )

    def create_or_replay(
        self,
        request_payload: dict,
        *,
        source_url: str,
        client_reference: str | None,
        idempotency_key: str,
        account_id,
        credential_id,
    ) -> dict:
        request_started_at = time.monotonic()
        key_hash = self._source_url_cipher.idempotency_key_hash(idempotency_key)
        fingerprint = self._source_url_cipher.payload_fingerprint(request_payload)
        job_id = self._reserve_job(
            key_hash=key_hash,
            payload_fingerprint=fingerprint,
            source_url=source_url,
            client_reference=client_reference,
            account_id=account_id,
            credential_id=credential_id,
        )
        return self._start_reserved_job(job_id, request_started_at)

    def _reserve_job(
        self,
        *,
        key_hash: str,
        payload_fingerprint: str,
        source_url: str,
        client_reference: str | None,
        account_id,
        credential_id,
    ) -> str:
        for _ in range(3):
            try:
                with self._sessions.begin() as session:
                    record = session.execute(
                        select(IdempotencyRecord)
                        .where(
                            IdempotencyRecord.account_id == account_id,
                            IdempotencyRecord.credential_id == credential_id,
                            IdempotencyRecord.idempotency_key_hash == key_hash,
                        )
                        .with_for_update()
                    ).scalar_one_or_none()
                    if record is not None and _is_expired(record.expires_at):
                        session.delete(record)
                        session.flush()
                        record = None

                    if record is not None:
                        if not hmac.compare_digest(
                            record.payload_fingerprint, payload_fingerprint
                        ):
                            raise IdempotencyKeyReused
                        return record.job_id

                    job_id = f"trn_{uuid4().hex}"
                    job = TranscriptionJob(
                        id=job_id,
                        account_id=account_id,
                        credential_id=credential_id,
                        status="downloading",
                        source_url_encrypted=self._source_url_cipher.encrypt(
                            job_id, source_url
                        ),
                        client_reference=client_reference,
                    )
                    session.add(job)
                    session.flush()
                    session.add(
                        IdempotencyRecord(
                            account_id=account_id,
                            credential_id=credential_id,
                            idempotency_key_hash=key_hash,
                            payload_fingerprint=payload_fingerprint,
                            job_id=job_id,
                        )
                    )
                    session.flush()
                    return job_id
            except IntegrityError as exc:
                # A concurrent request won the unique scope/key constraint. Read it
                # on the next iteration and compare the semantic request payload.
                diagnostic = getattr(exc.orig, "diag", None)
                if (
                    getattr(diagnostic, "constraint_name", None)
                    != "uq_idempotency_scope_key"
                ):
                    raise
                continue
        raise RuntimeError("Não foi possível reservar a chave de idempotência.")

    def _start_reserved_job(self, job_id: str, request_started_at: float) -> dict:
        deferred_error: Exception | None = None
        public_job: dict | None = None
        with self._sessions.begin() as session:
            record = session.execute(
                select(IdempotencyRecord)
                .where(IdempotencyRecord.job_id == job_id)
                .with_for_update()
            ).scalar_one_or_none()
            job = session.execute(
                select(TranscriptionJob)
                .where(TranscriptionJob.id == job_id)
                .with_for_update()
            ).scalar_one_or_none()
            if record is None or job is None:
                raise RuntimeError("Reserva durável do job não encontrada.")

            now = _utc_now()
            if record.expires_at is not None and record.expires_at > now:
                return _public_job(job)

            source_url = self._source_url_cipher.decrypt(
                job.id, job.source_url_encrypted
            )
            try:
                self._download_executor.submit(
                    self._source_connector.start, source_url
                ).result()
            except (ExpiredSourceURL, InvalidSourceURL, MediaSizeExceeded) as exc:
                session.delete(record)
                session.delete(job)
                session.flush()
                deferred_error = exc
            except TransientSourceFailure as exc:
                # Q-04: a transient origin failure at admission does not reject
                # the job. This attempt counts as the first of the three
                # permitted origin attempts; the downloader owns the retries
                # with jitter and fails terminally when they are exhausted.
                # Preserve Retry-After and schedule the earliest instant for
                # worker attempt 2 with full jitter 0-5 s, so the retry does
                # not start immediately.
                accepted_at = _utc_now()
                retry_after = exc.retry_after_seconds or 0.0
                jitter = self._jitter(0.0, ADMISSION_RETRY_JITTER_SECONDS)
                delay = max(retry_after, jitter)
                job.download_attempts += 1
                job.accepted_at = accepted_at
                job.source_connection_started_at = accepted_at
                job.updated_at = accepted_at
                job.download_not_before = accepted_at + timedelta(seconds=delay)
                record.expires_at = accepted_at + IDEMPOTENCY_WINDOW
                public_job = _public_job(job)
            except SourceUnavailable as exc:
                # Permanent origin condition known at admission: reject with
                # the contractual error instead of keeping a reservation the
                # worker would repeat outside the retry policy.
                session.delete(record)
                session.delete(job)
                session.flush()
                deferred_error = exc
            else:
                accepted_at = _utc_now()
                # The successful admission GET is the first of the three
                # permitted origin attempts; the worker owns only retries.
                job.download_attempts += 1
                job.accepted_at = accepted_at
                job.source_connection_started_at = accepted_at
                job.updated_at = accepted_at
                job.download_not_before = None
                record.expires_at = accepted_at + IDEMPOTENCY_WINDOW
                public_job = _public_job(job)

        if deferred_error is not None:
            raise deferred_error
        if public_job is None:
            raise RuntimeError("O job não foi aceito.")
        self._download_metrics.observe_start(time.monotonic() - request_started_at)
        return public_job

    def get(self, job_id: str, *, account_id, credential_id) -> dict | None:
        with self._sessions() as session:
            job = session.execute(
                select(TranscriptionJob).where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.account_id == account_id,
                    TranscriptionJob.credential_id == credential_id,
                )
            ).scalar_one_or_none()
            return _public_job(job) if job is not None else None

    def source_url_for_recovery(self, job_id: str) -> str | None:
        """Return a durable URL to the owning worker without exposing it over HTTP."""
        with self._sessions() as session:
            job = session.get(TranscriptionJob, job_id)
            if (
                job is None
                or job.status != "downloading"
                or job.source_url_encrypted is None
            ):
                return None
            return self._source_url_cipher.decrypt(job.id, job.source_url_encrypted)

    def shutdown(self) -> None:
        """Wait for source connection tasks to stop before disposing the engine."""
        self._download_executor.shutdown(wait=True)


def _is_expired(expires_at: datetime | None) -> bool:
    return expires_at is not None and expires_at <= _utc_now()


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


def _isoformat(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _public_job(job: TranscriptionJob) -> dict:
    result = {
        "jobId": job.id,
        "status": job.status,
        "createdAt": _isoformat(job.created_at),
        "updatedAt": _isoformat(job.updated_at),
        "statusUrl": f"/v1/transcriptions/{job.id}",
    }
    if job.client_reference is not None:
        result["clientReference"] = job.client_reference
    if job.terminal_at is not None:
        result["terminalAt"] = _isoformat(job.terminal_at)
    if job.failure_code is not None:
        result["failure"] = {
            "code": job.failure_code,
            "message": _failure_message(job.failure_code),
        }
    return result


def _failure_message(code: str) -> str:
    return {
        "SOURCE_UNAVAILABLE": "Não foi possível obter a mídia na origem.",
        "MEDIA_SIZE_LIMIT_EXCEEDED": "A mídia excede o limite permitido de 5 GiB.",
        "MEDIA_DURATION_LIMIT_EXCEEDED": "A mídia excede a duração máxima de 2 horas.",
        "UNSUPPORTED_MEDIA_FORMAT": "O formato ou codec da mídia não é compatível.",
        "TRANSCRIPTION_FAILED": "Não foi possível transcrever a mídia.",
    }.get(code, "O processamento da mídia falhou.")
