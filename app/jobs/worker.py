"""Recoverable downloader and serial transcription workers."""

from __future__ import annotations

import json
import logging
import random
import tempfile
import threading
import time
from contextlib import contextmanager
from datetime import datetime, timedelta, timezone
from pathlib import Path
from urllib.parse import urlsplit
from uuid import uuid4

from sqlalchemy import func, or_, select, update
from sqlalchemy.engine import Engine
from sqlalchemy.orm import sessionmaker

from app.jobs.models import TranscriptionJob
from app.jobs.security import SourceURLCipher
from app.monitoring.metrics import JobProcessingMetrics, ResourceSampler
from app.service import public_result_payload
from app.storage.s3 import S3ObjectStore
from app.transcription.paths import InvalidSourceURL
from app.transcription.source import (
    MediaSizeExceeded,
    SourceConnector,
    SourceUnavailable,
    TransientSourceFailure,
    source_url_expiry,
)
from app.transcription.validation import (
    MediaDurationExceeded,
    UnsupportedMediaFormat,
    inspect_media,
)


logger = logging.getLogger(__name__)
LEASE_SECONDS = 120
LEASE_RENEW_SECONDS = 30
DOWNLOAD_CONCURRENCY = 2
MAX_DOWNLOAD_ATTEMPTS = 3
DOWNLOAD_RETRY_WINDOWS = (5.0, 30.0)
DOWNLOAD_ADVISORY_LOCK = 4_377_739_212_782
PROCESSING_ADVISORY_LOCK = 4_377_739_212_783


class JobWorker:
    """Coordinate up to two downloads and one serial transcription per process."""

    def __init__(
        self,
        engine: Engine,
        source_url_cipher: SourceURLCipher,
        source_connector: SourceConnector,
        object_store: S3ObjectStore,
        transcriber,
        metrics: JobProcessingMetrics,
        *,
        sleeper=time.sleep,
        jitter=random.uniform,
    ) -> None:
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._source_url_cipher = source_url_cipher
        self._source_connector = source_connector
        self._object_store = object_store
        self._transcriber = transcriber
        self._metrics = metrics
        self._sleep = sleeper
        self._jitter = jitter
        self._stop = threading.Event()
        self._threads: list[threading.Thread] = []

    def start(self) -> None:
        if self._threads:
            return
        for index in range(DOWNLOAD_CONCURRENCY):
            self._threads.append(
                threading.Thread(
                    target=self._download_loop,
                    name=f"media-download-{index + 1}",
                    daemon=True,
                )
            )
        self._threads.append(
            threading.Thread(
                target=self._processing_loop,
                name="media-transcription",
                daemon=True,
            )
        )
        for thread in self._threads:
            thread.start()

    def shutdown(self) -> None:
        self._stop.set()
        for thread in self._threads:
            thread.join(timeout=READ_SHUTDOWN_TIMEOUT_SECONDS)
        self._threads.clear()

    def run_download_once(self) -> bool:
        claimed = self._claim_download()
        if claimed is None:
            return False
        job_id, encrypted_url, owner, claimed_at = claimed
        self._metrics.increment("downloadsStarted")
        with _lease_heartbeat(self, job_id, owner):
            try:
                source_url = self._source_url_cipher.decrypt(job_id, encrypted_url)
                url_expiry = source_url_expiry(source_url)
                suffix = Path(urlsplit(source_url).path).suffix.casefold()
                if not suffix or len(suffix) > 10:
                    suffix = ".bin"
                with tempfile.TemporaryDirectory(prefix="whisper-media-") as directory:
                    media_path = Path(directory) / f"source{suffix}"
                    self._download_with_retries(
                        job_id, owner, source_url, media_path, url_expiry
                    )
                    inspect_media(media_path)
                    object_key = self._object_store.upload_media(
                        job_id, media_path, suffix=suffix
                    )
                if self._mark_queued(job_id, owner, object_key):
                    self._metrics.increment("downloadsQueued")
                    self._metrics.observe_duration(
                        "download", _elapsed_since_claim(claimed_at)
                    )
                else:
                    self._object_store.delete(object_key)
            except (InvalidSourceURL, SourceUnavailable):
                self._finish_failure(job_id, owner, "SOURCE_UNAVAILABLE", "download")
            except MediaSizeExceeded:
                self._finish_failure(
                    job_id, owner, "MEDIA_SIZE_LIMIT_EXCEEDED", "download"
                )
            except UnsupportedMediaFormat:
                self._finish_failure(
                    job_id, owner, "UNSUPPORTED_MEDIA_FORMAT", "download"
                )
            except MediaDurationExceeded:
                self._finish_failure(
                    job_id, owner, "MEDIA_DURATION_LIMIT_EXCEEDED", "download"
                )
            except Exception as exc:
                self._release_lease(job_id, owner)
                logger.warning(
                    "Job download will be retried job_id=%s error_type=%s",
                    job_id,
                    type(exc).__name__,
                )
                return True
        return True

    def run_processing_once(self) -> bool:
        claimed = self._claim_processing()
        if claimed is None:
            return False
        job_id, media_key, owner, claimed_at, queue_wait_seconds = claimed
        self._metrics.increment("processingStarted")
        self._metrics.observe_duration("queueWait", queue_wait_seconds)
        with _lease_heartbeat(self, job_id, owner):
            try:
                suffix = Path(media_key).suffix or ".bin"
                with tempfile.TemporaryDirectory(
                    prefix="whisper-processing-"
                ) as directory:
                    media_path = Path(directory) / f"source{suffix}"
                    self._object_store.download_media(media_key, media_path)
                    duration_seconds = inspect_media(media_path)
                    sampler = ResourceSampler(interval_seconds=1.0)
                    sampler.start()
                    try:
                        segments, _ = self._transcriber.transcribe(media_path)
                    except Exception as exc:
                        raise _TranscriptionEngineFailure from exc
                    finally:
                        inference_seconds = sampler.stop()
                        self._metrics.observe_resources(sampler.summary())
                    payload = public_result_payload(job_id, segments, duration_seconds)
                    result_key = self._object_store.put_result(
                        job_id,
                        json.dumps(
                            payload, ensure_ascii=False, separators=(",", ":")
                        ).encode("utf-8"),
                    )
                completed = self._mark_completed(job_id, owner, result_key, media_key)
                if completed:
                    self._object_store.delete(media_key)
                    self._metrics.increment("processingCompleted")
                    elapsed = _elapsed_since_claim(claimed_at)
                    self._metrics.observe_duration("processing", elapsed)
                    self._metrics.observe_rtf(
                        inference_seconds / duration_seconds
                        if duration_seconds > 0
                        else None
                    )
                else:
                    self._object_store.delete(result_key)
            except MediaDurationExceeded:
                self._delete_object_safely(media_key, job_id)
                self._finish_failure(
                    job_id, owner, "MEDIA_DURATION_LIMIT_EXCEEDED", "processing"
                )
            except UnsupportedMediaFormat:
                self._delete_object_safely(media_key, job_id)
                self._finish_failure(
                    job_id, owner, "UNSUPPORTED_MEDIA_FORMAT", "processing"
                )
            except _TranscriptionEngineFailure:
                self._delete_object_safely(media_key, job_id)
                self._finish_failure(
                    job_id, owner, "TRANSCRIPTION_FAILED", "processing"
                )
            except Exception as exc:
                self._release_lease(job_id, owner)
                logger.warning(
                    "Job processing will be retried job_id=%s error_type=%s",
                    job_id,
                    type(exc).__name__,
                )
                return True
        return True

    def _download_with_retries(
        self,
        job_id: str,
        owner: str,
        source_url: str,
        destination: Path,
        expires_at: datetime | None,
    ) -> None:
        self._honor_admission_backoff(job_id, owner, expires_at)
        while True:
            if expires_at is not None and _utc_now() >= expires_at:
                raise SourceUnavailable
            attempt = self._record_download_attempt(job_id, owner)
            if attempt is None:
                raise SourceUnavailable
            try:
                self._source_connector.download_to_path(source_url, destination)
                return
            except TransientSourceFailure as exc:
                if attempt >= MAX_DOWNLOAD_ATTEMPTS:
                    raise SourceUnavailable from None
                retry_after = exc.retry_after_seconds or 0.0
                retry_index = max(0, attempt - 2)
                jitter = self._jitter(0.0, DOWNLOAD_RETRY_WINDOWS[retry_index])
                delay = max(retry_after, jitter)
                if (
                    expires_at is not None
                    and _utc_now() + timedelta(seconds=delay) >= expires_at
                ):
                    raise SourceUnavailable from None
                destination.unlink(missing_ok=True)
                self._sleep(delay)
            except (InvalidSourceURL, MediaSizeExceeded, SourceUnavailable):
                raise

    def _honor_admission_backoff(
        self,
        job_id: str,
        owner: str,
        expires_at: datetime | None,
    ) -> None:
        """Sleep the Q-04 wait scheduled at admission before worker attempt 2.

        The admission GET counts as attempt 1 of 3; when it fails transiently
        the executor persists ``download_not_before`` with
        ``max(Retry-After, full jitter 0-5 s)``. The claim filter already skips
        jobs that are not due, but honor the remaining wait here as well so a
        claimed job never starts its next attempt early.
        """
        with self._sessions.begin() as session:
            not_before = session.scalar(
                select(TranscriptionJob.download_not_before).where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                )
            )
        if not_before is None:
            return
        if not_before.tzinfo is None:
            not_before = not_before.replace(tzinfo=timezone.utc)
        now = _utc_now()
        delay = (not_before - now).total_seconds()
        if delay <= 0:
            self._clear_admission_backoff(job_id, owner)
            return
        if expires_at is not None and now + timedelta(seconds=delay) >= expires_at:
            self._clear_admission_backoff(job_id, owner)
            raise SourceUnavailable from None
        self._sleep(delay)
        self._clear_admission_backoff(job_id, owner)

    def _clear_admission_backoff(self, job_id: str, owner: str) -> None:
        with self._sessions.begin() as session:
            session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                )
                .values(download_not_before=None)
            )

    def _record_download_attempt(self, job_id: str, owner: str) -> int | None:
        with self._sessions.begin() as session:
            return session.scalar(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                    TranscriptionJob.download_attempts < MAX_DOWNLOAD_ATTEMPTS,
                )
                .values(download_attempts=TranscriptionJob.download_attempts + 1)
                .returning(TranscriptionJob.download_attempts)
            )

    def _claim_download(self):
        now = _utc_now()
        owner = uuid4().hex
        with self._sessions.begin() as session:
            session.execute(select(func.pg_advisory_xact_lock(DOWNLOAD_ADVISORY_LOCK)))
            active_count = session.scalar(
                select(func.count())
                .select_from(TranscriptionJob)
                .where(
                    TranscriptionJob.status == "downloading",
                    TranscriptionJob.lease_expires_at > now,
                )
            )
            if active_count >= DOWNLOAD_CONCURRENCY:
                return None
            job = session.execute(
                select(TranscriptionJob)
                .where(
                    TranscriptionJob.status == "downloading",
                    TranscriptionJob.source_url_encrypted.is_not(None),
                    or_(
                        TranscriptionJob.lease_expires_at.is_(None),
                        TranscriptionJob.lease_expires_at <= now,
                    ),
                    or_(
                        TranscriptionJob.download_not_before.is_(None),
                        TranscriptionJob.download_not_before <= now,
                    ),
                )
                .order_by(TranscriptionJob.created_at)
                .limit(1)
                .with_for_update(skip_locked=True)
            ).scalar_one_or_none()
            if job is None:
                return None
            job.lease_owner = owner
            job.lease_expires_at = now + timedelta(seconds=LEASE_SECONDS)
            return job.id, job.source_url_encrypted, owner, time.monotonic()

    def _claim_processing(self):
        now = _utc_now()
        owner = uuid4().hex
        with self._sessions.begin() as session:
            session.execute(
                select(func.pg_advisory_xact_lock(PROCESSING_ADVISORY_LOCK))
            )
            active_count = session.scalar(
                select(func.count())
                .select_from(TranscriptionJob)
                .where(
                    TranscriptionJob.status == "processing",
                    TranscriptionJob.lease_expires_at > now,
                )
            )
            if active_count:
                return None
            job = session.execute(
                select(TranscriptionJob)
                .where(
                    TranscriptionJob.status.in_(("queued", "processing")),
                    TranscriptionJob.media_object_key.is_not(None),
                    or_(
                        TranscriptionJob.lease_expires_at.is_(None),
                        TranscriptionJob.lease_expires_at <= now,
                    ),
                )
                .order_by(TranscriptionJob.queued_at, TranscriptionJob.created_at)
                .limit(1)
                .with_for_update(skip_locked=True)
            ).scalar_one_or_none()
            if job is None:
                return None
            if job.status == "queued":
                job.status = "processing"
                job.processing_started_at = now
                job.updated_at = now
            job.lease_owner = owner
            job.lease_expires_at = now + timedelta(seconds=LEASE_SECONDS)
            return (
                job.id,
                job.media_object_key,
                owner,
                time.monotonic(),
                max(0.0, (now - job.queued_at).total_seconds()),
            )

    def _renew_lease(self, job_id: str, owner: str) -> None:
        with self._sessions.begin() as session:
            session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                )
                .values(lease_expires_at=_utc_now() + timedelta(seconds=LEASE_SECONDS))
            )

    def _mark_queued(self, job_id: str, owner: str, media_key: str) -> bool:
        now = _utc_now()
        with self._sessions.begin() as session:
            result = session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.status == "downloading",
                    TranscriptionJob.lease_owner == owner,
                )
                .values(
                    status="queued",
                    media_object_key=media_key,
                    source_url_encrypted=None,
                    download_not_before=None,
                    queued_at=now,
                    updated_at=now,
                    lease_owner=None,
                    lease_expires_at=None,
                )
            )
            return result.rowcount == 1

    def _mark_completed(
        self, job_id: str, owner: str, result_key: str, media_key: str
    ) -> bool:
        now = _utc_now()
        with self._sessions.begin() as session:
            transition = session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.status == "processing",
                    TranscriptionJob.lease_owner == owner,
                )
                .values(
                    status="completed",
                    result_object_key=result_key,
                    media_object_key=None,
                    terminal_at=now,
                    updated_at=now,
                    lease_owner=None,
                    lease_expires_at=None,
                )
                .returning(TranscriptionJob.id, TranscriptionJob.accepted_at)
            ).one_or_none()
        if transition is None:
            return False
        accepted_at = transition[1]
        if accepted_at is not None:
            self._metrics.observe_duration(
                "acceptedToTerminal", _elapsed_between(accepted_at, now)
            )
        return True

    def _finish_failure(self, job_id: str, owner: str, code: str, stage: str) -> None:
        now = _utc_now()
        with self._sessions.begin() as session:
            transition = session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                    TranscriptionJob.status.in_(("downloading", "processing")),
                )
                .values(
                    status="failed",
                    failure_code=code,
                    source_url_encrypted=None,
                    download_not_before=None,
                    media_object_key=None,
                    terminal_at=now,
                    updated_at=now,
                    lease_owner=None,
                    lease_expires_at=None,
                )
                .returning(TranscriptionJob.id, TranscriptionJob.accepted_at)
            ).one_or_none()
        if transition is not None:
            self._metrics.increment(f"{stage}Failed")
            self._metrics.observe_failure(stage, code)
            accepted_at = transition[1]
            if accepted_at is not None:
                self._metrics.observe_duration(
                    "acceptedToTerminal", _elapsed_between(accepted_at, now)
                )

    def _release_lease(self, job_id: str, owner: str) -> None:
        with self._sessions.begin() as session:
            session.execute(
                update(TranscriptionJob)
                .where(
                    TranscriptionJob.id == job_id,
                    TranscriptionJob.lease_owner == owner,
                )
                .values(lease_owner=None, lease_expires_at=None)
            )

    def _download_loop(self) -> None:
        while not self._stop.is_set():
            try:
                did_work = self.run_download_once()
            except Exception as exc:
                logger.warning(
                    "Could not claim a download job error_type=%s",
                    type(exc).__name__,
                )
                did_work = False
            if not did_work:
                self._stop.wait(0.5)

    def _processing_loop(self) -> None:
        while not self._stop.is_set():
            try:
                did_work = self.run_processing_once()
            except Exception as exc:
                logger.warning(
                    "Could not claim a processing job error_type=%s",
                    type(exc).__name__,
                )
                did_work = False
            if not did_work:
                self._stop.wait(0.5)

    def _delete_object_safely(self, key: str | None, job_id: str) -> None:
        try:
            self._object_store.delete(key)
        except Exception as exc:
            logger.warning(
                "Could not remove temporary object job_id=%s error_type=%s",
                job_id,
                type(exc).__name__,
            )


READ_SHUTDOWN_TIMEOUT_SECONDS = 65


@contextmanager
def _lease_heartbeat(worker: JobWorker, job_id: str, owner: str):
    stop = threading.Event()

    def renew() -> None:
        while not stop.wait(LEASE_RENEW_SECONDS):
            try:
                worker._renew_lease(job_id, owner)
            except Exception as exc:
                logger.warning(
                    "Could not renew job lease job_id=%s error_type=%s",
                    job_id,
                    type(exc).__name__,
                )

    thread = threading.Thread(target=renew, name=f"job-lease-{job_id}", daemon=True)
    thread.start()
    try:
        yield
    finally:
        stop.set()
        thread.join(timeout=1)


class _TranscriptionEngineFailure(RuntimeError):
    """The transcription adapter failed after storage and media validation."""


def _elapsed_since_claim(started_at: float) -> float:
    return max(0.0, time.monotonic() - started_at)


def _elapsed_between(started_at: datetime, finished_at: datetime) -> float:
    if started_at.tzinfo is None:
        started_at = started_at.replace(tzinfo=timezone.utc)
    return max(0.0, (finished_at - started_at).total_seconds())


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)
