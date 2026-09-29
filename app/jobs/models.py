"""Durable transcription job and idempotency records."""

from __future__ import annotations

from datetime import datetime, timezone
from uuid import UUID, uuid4

from sqlalchemy import (
    DateTime,
    ForeignKey,
    Index,
    LargeBinary,
    String,
    Text,
    Uuid,
    UniqueConstraint,
    func,
)
from sqlalchemy.orm import Mapped, mapped_column

from app.database import Base


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class TranscriptionJob(Base):
    __tablename__ = "transcription_jobs"

    id: Mapped[str] = mapped_column(String(40), primary_key=True)
    account_id: Mapped[UUID] = mapped_column(
        Uuid(as_uuid=True),
        ForeignKey("accounts.id", ondelete="CASCADE"),
        nullable=False,
    )
    credential_id: Mapped[UUID] = mapped_column(
        Uuid(as_uuid=True),
        ForeignKey("credentials.id", ondelete="CASCADE"),
        nullable=False,
    )
    status: Mapped[str] = mapped_column(String(24), nullable=False)
    source_url_encrypted: Mapped[bytes] = mapped_column(LargeBinary, nullable=False)
    client_reference: Mapped[str | None] = mapped_column(Text)
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=_utc_now,
        server_default=func.now(),
    )
    updated_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True), nullable=False, default=_utc_now, onupdate=_utc_now
    )
    accepted_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
    source_connection_started_at: Mapped[datetime | None] = mapped_column(
        DateTime(timezone=True)
    )


class IdempotencyRecord(Base):
    __tablename__ = "idempotency_records"
    __table_args__ = (
        UniqueConstraint(
            "account_id",
            "credential_id",
            "idempotency_key_hash",
            name="uq_idempotency_scope_key",
        ),
        Index("ix_idempotency_records_expires_at", "expires_at"),
    )

    id: Mapped[UUID] = mapped_column(
        Uuid(as_uuid=True), primary_key=True, default=uuid4
    )
    account_id: Mapped[UUID] = mapped_column(
        Uuid(as_uuid=True),
        ForeignKey("accounts.id", ondelete="CASCADE"),
        nullable=False,
    )
    credential_id: Mapped[UUID] = mapped_column(
        Uuid(as_uuid=True),
        ForeignKey("credentials.id", ondelete="CASCADE"),
        nullable=False,
    )
    idempotency_key_hash: Mapped[str] = mapped_column(String(64), nullable=False)
    payload_fingerprint: Mapped[str] = mapped_column(String(64), nullable=False)
    job_id: Mapped[str] = mapped_column(
        String(40),
        ForeignKey("transcription_jobs.id", ondelete="CASCADE"),
        nullable=False,
    )
    created_at: Mapped[datetime] = mapped_column(
        DateTime(timezone=True),
        nullable=False,
        default=_utc_now,
        server_default=func.now(),
    )
    expires_at: Mapped[datetime | None] = mapped_column(DateTime(timezone=True))
