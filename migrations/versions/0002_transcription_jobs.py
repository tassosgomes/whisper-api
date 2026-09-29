"""Persist jobs and their idempotency window.

Revision ID: 0002_transcription_jobs
Revises: 0001_access_credentials
Create Date: 2026-09-28
"""

from __future__ import annotations

from typing import Sequence

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql


revision: str = "0002_transcription_jobs"
down_revision: str | None = "0001_access_credentials"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.create_table(
        "transcription_jobs",
        sa.Column("id", sa.String(length=40), primary_key=True, nullable=False),
        sa.Column(
            "account_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("accounts.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column(
            "credential_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("credentials.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("status", sa.String(length=24), nullable=False),
        sa.Column("source_url_encrypted", sa.LargeBinary(), nullable=False),
        sa.Column("client_reference", sa.Text(), nullable=True),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.func.now(),
            nullable=False,
        ),
        sa.Column(
            "updated_at",
            sa.DateTime(timezone=True),
            server_default=sa.func.now(),
            nullable=False,
        ),
        sa.Column("accepted_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column(
            "source_connection_started_at", sa.DateTime(timezone=True), nullable=True
        ),
    )
    op.create_index(
        "ix_transcription_jobs_account_id",
        "transcription_jobs",
        ["account_id"],
    )
    op.create_index(
        "ix_transcription_jobs_credential_id",
        "transcription_jobs",
        ["credential_id"],
    )

    op.create_table(
        "idempotency_records",
        sa.Column(
            "id", postgresql.UUID(as_uuid=True), primary_key=True, nullable=False
        ),
        sa.Column(
            "account_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("accounts.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column(
            "credential_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("credentials.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("idempotency_key_hash", sa.String(length=64), nullable=False),
        sa.Column("payload_fingerprint", sa.String(length=64), nullable=False),
        sa.Column(
            "job_id",
            sa.String(length=40),
            sa.ForeignKey("transcription_jobs.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.func.now(),
            nullable=False,
        ),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=True),
        sa.UniqueConstraint(
            "account_id",
            "credential_id",
            "idempotency_key_hash",
            name="uq_idempotency_scope_key",
        ),
    )
    op.create_index(
        "ix_idempotency_records_expires_at", "idempotency_records", ["expires_at"]
    )


def downgrade() -> None:
    op.drop_index("ix_idempotency_records_expires_at", table_name="idempotency_records")
    op.drop_table("idempotency_records")
    op.drop_index(
        "ix_transcription_jobs_credential_id", table_name="transcription_jobs"
    )
    op.drop_index("ix_transcription_jobs_account_id", table_name="transcription_jobs")
    op.drop_table("transcription_jobs")
