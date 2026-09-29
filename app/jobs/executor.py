"""Durable job acceptance and idempotency handling."""

from __future__ import annotations

import hmac
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
    MediaSizeExceeded,
    SourceConnector,
    SourceUnavailable,
)


IDEMPOTENCY_WINDOW = timedelta(seconds=120)
DOWNLOAD_CONNECT_CONCURRENCY = 2


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
    ) -> None:
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._source_url_cipher = source_url_cipher
        self._source_connector = source_connector
        self._download_metrics = download_metrics
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
            except (InvalidSourceURL, MediaSizeExceeded) as exc:
                session.delete(record)
                session.delete(job)
                session.flush()
                deferred_error = exc
            except SourceUnavailable as exc:
                # Keep the durable reservation. A retry or process recovery can
                # restart the connection attempt without losing the source URL.
                deferred_error = exc
            else:
                accepted_at = _utc_now()
                job.accepted_at = accepted_at
                job.source_connection_started_at = accepted_at
                job.updated_at = accepted_at
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
            if job is None or job.status != "downloading":
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
    return result
