"""Durable, credential-scoped requests for direct media uploads."""

from __future__ import annotations

import hmac
from datetime import datetime, timedelta, timezone
from uuid import uuid4

from sqlalchemy import func, select
from sqlalchemy.engine import Engine
from sqlalchemy.orm import sessionmaker

from app.access.models import Credential
from app.jobs.executor import IdempotencyKeyReused, _public_job, create_uploaded_job
from app.jobs.models import TranscriptionJob, TranscriptionUpload
from app.jobs.security import SourceURLCipher
from app.storage.s3 import S3ObjectStore


UPLOAD_RETENTION = timedelta(hours=24)
CONTENT_TYPE_SUFFIXES = {
    "audio/wav": ".wav",
    "audio/mpeg": ".mp3",
    "audio/mp4": ".m4a",
    "video/mp4": ".mp4",
    "video/webm": ".webm",
    "video/x-matroska": ".mkv",
}


class UploadRejected(ValueError):
    """A rejected upload requires a fresh idempotency key."""


class TooManyPendingUploads(ValueError):
    """Admission is bounded per credential, including concurrent requests."""


class UploadNotFound(ValueError):
    """An absent, expired or inaccessible upload has the same public response."""


class UploadNotReceived(ValueError):
    """No object is available yet; the pending upload can still be retried."""


class UploadMismatch(ValueError):
    def __init__(self, code: str) -> None:
        self.code = code
        super().__init__(code)


class UploadManager:
    def __init__(
        self,
        engine: Engine,
        cipher: SourceURLCipher,
        object_store: S3ObjectStore,
        *,
        url_ttl_seconds: int = 3600,
        pending_limit: int = 5,
    ) -> None:
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._cipher = cipher
        self._object_store = object_store
        self.url_ttl_seconds = url_ttl_seconds
        self.pending_limit = pending_limit

    def complete(self, upload_id: str, *, account_id, credential_id) -> dict:
        failure_code = None
        with self._sessions.begin() as session:
            record = session.execute(
                select(TranscriptionUpload)
                .where(
                    TranscriptionUpload.id == upload_id,
                    TranscriptionUpload.account_id == account_id,
                    TranscriptionUpload.credential_id == credential_id,
                )
                .with_for_update()
            ).scalar_one_or_none()
            now = datetime.now(timezone.utc)
            if record is None or (
                record.status != "completed" and record.expires_at <= now
            ):
                raise UploadNotFound
            if record.status == "completed":
                return _public_job(session.get(TranscriptionJob, record.job_id))
            if record.status == "rejected":
                failure_code = record.failure_code
                # The still-valid PUT URL may have been used after rejection.
                self._object_store.delete(record.object_key)
            else:
                stored = self._object_store.inspect_upload(record.object_key)
                if stored is None:
                    raise UploadNotReceived
                if stored.size_bytes != record.size_bytes:
                    failure_code = "UPLOAD_SIZE_MISMATCH"
                elif stored.checksum_sha256 is None or not hmac.compare_digest(
                    stored.checksum_sha256, record.checksum_sha256
                ):
                    failure_code = "UPLOAD_CHECKSUM_MISMATCH"
                elif not _matches_format(record.content_type, stored.first_bytes):
                    failure_code = "UNSUPPORTED_MEDIA_FORMAT"
                if failure_code is None:
                    return create_uploaded_job(session, record, now)
                record.status = "rejected"
                record.failure_code = failure_code
                record.updated_at = now
                self._object_store.delete(record.object_key)
        # Raise after committing the durable refusal, so replay stays identical.
        raise UploadMismatch(failure_code)

    def create_or_replay(
        self,
        payload: dict,
        *,
        idempotency_key: str,
        account_id,
        credential_id,
    ) -> tuple[dict, bool]:
        key_hash = self._cipher.idempotency_key_hash(idempotency_key)
        fingerprint = self._cipher.payload_fingerprint(payload)
        with self._sessions.begin() as session:
            # Also locks the empty-set case: two new keys cannot exceed the limit.
            session.execute(
                select(Credential.id)
                .where(
                    Credential.id == credential_id, Credential.account_id == account_id
                )
                .with_for_update()
            ).scalar_one()
            now = datetime.now(timezone.utc)
            record = session.execute(
                select(TranscriptionUpload)
                .where(
                    TranscriptionUpload.account_id == account_id,
                    TranscriptionUpload.credential_id == credential_id,
                    TranscriptionUpload.idempotency_key_hash == key_hash,
                )
                .with_for_update()
            ).scalar_one_or_none()
            if (
                record is not None
                and record.status == "pending"
                and record.expires_at <= now
            ):
                session.delete(record)
                session.flush()
                record = None
            created = record is None
            if record is not None:
                if not hmac.compare_digest(record.payload_fingerprint, fingerprint):
                    raise IdempotencyKeyReused
                if record.status == "rejected":
                    raise UploadRejected
            else:
                pending = session.scalar(
                    select(func.count())
                    .select_from(TranscriptionUpload)
                    .where(
                        TranscriptionUpload.account_id == account_id,
                        TranscriptionUpload.credential_id == credential_id,
                        TranscriptionUpload.status == "pending",
                        TranscriptionUpload.expires_at > now,
                    )
                )
                if pending >= self.pending_limit:
                    raise TooManyPendingUploads
                upload_id = f"upl_{uuid4().hex}"
                record = TranscriptionUpload(
                    id=upload_id,
                    account_id=account_id,
                    credential_id=credential_id,
                    idempotency_key_hash=key_hash,
                    payload_fingerprint=fingerprint,
                    content_type=payload["contentType"],
                    size_bytes=payload["sizeBytes"],
                    checksum_sha256=payload["checksumSha256"],
                    duration_ms=payload.get("durationMs"),
                    client_reference=payload.get("clientReference"),
                    object_key=self._object_store.upload_key(
                        upload_id, CONTENT_TYPE_SUFFIXES[payload["contentType"]]
                    ),
                    status="pending",
                    created_at=now,
                    updated_at=now,
                    expires_at=now + UPLOAD_RETENTION,
                )
                session.add(record)
                session.flush()
            body = {"uploadId": record.id, "expiresAt": _iso(record.expires_at)}
            if record.client_reference is not None:
                body["clientReference"] = record.client_reference
            if record.status == "completed":
                body["jobId"] = record.job_id
            else:
                ttl = min(
                    self.url_ttl_seconds, int((record.expires_at - now).total_seconds())
                )
                body["upload"] = self._object_store.presign_upload(
                    record.object_key,
                    content_type=record.content_type,
                    size_bytes=record.size_bytes,
                    checksum_sha256=record.checksum_sha256,
                    expires_in=ttl,
                )
                body["expiresAt"] = _iso(now + timedelta(seconds=ttl))
                body["completeUrl"] = f"/v1/transcription-uploads/{record.id}/complete"
        return body, created


def _iso(value: datetime) -> str:
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")


def _matches_format(content_type: str, header: bytes) -> bool:
    """Check container magic only; decoding and duration belong to the worker."""
    if content_type == "audio/wav":
        return header[:4] == b"RIFF" and header[8:12] == b"WAVE"
    if content_type == "audio/mpeg":
        return header.startswith(b"ID3") or (
            len(header) >= 4
            and header[0] == 0xFF
            and header[1] & 0xE0 == 0xE0
            and header[1] & 0x18 != 0x08
            and header[1] & 0x06 != 0
            and header[2] >> 4 not in (0, 15)
            and header[2] & 0x0C != 0x0C
        )
    if content_type in {"audio/mp4", "video/mp4"}:
        return len(header) >= 12 and header[4:8] == b"ftyp"
    if content_type in {"video/webm", "video/x-matroska"}:
        return header.startswith(b"\x1a\x45\xdf\xa3")
    return False
