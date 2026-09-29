"""Track recoverable media acquisition and processing work.

Revision ID: 0003_media_processing
Revises: 0002_transcription_jobs
Create Date: 2026-09-28
"""

from __future__ import annotations

from typing import Sequence

from alembic import op
import sqlalchemy as sa


revision: str = "0003_media_processing"
down_revision: str | None = "0002_transcription_jobs"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.alter_column(
        "transcription_jobs",
        "source_url_encrypted",
        existing_type=sa.LargeBinary(),
        nullable=True,
    )
    op.add_column("transcription_jobs", sa.Column("media_object_key", sa.String(512)))
    op.add_column("transcription_jobs", sa.Column("result_object_key", sa.String(512)))
    op.add_column("transcription_jobs", sa.Column("failure_code", sa.String(64)))
    op.add_column(
        "transcription_jobs", sa.Column("queued_at", sa.DateTime(timezone=True))
    )
    op.add_column(
        "transcription_jobs",
        sa.Column("processing_started_at", sa.DateTime(timezone=True)),
    )
    op.add_column(
        "transcription_jobs", sa.Column("terminal_at", sa.DateTime(timezone=True))
    )
    op.add_column("transcription_jobs", sa.Column("lease_owner", sa.String(64)))
    op.add_column(
        "transcription_jobs",
        sa.Column("lease_expires_at", sa.DateTime(timezone=True)),
    )
    op.add_column(
        "transcription_jobs",
        sa.Column(
            "download_attempts", sa.Integer(), nullable=False, server_default="0"
        ),
    )
    op.execute(
        "UPDATE transcription_jobs SET download_attempts = 1 "
        "WHERE status = 'downloading' AND accepted_at IS NOT NULL"
    )
    op.create_index(
        "ix_transcription_jobs_status_lease",
        "transcription_jobs",
        ["status", "lease_expires_at"],
    )


def downgrade() -> None:
    op.drop_index("ix_transcription_jobs_status_lease", table_name="transcription_jobs")
    op.drop_column("transcription_jobs", "download_attempts")
    op.drop_column("transcription_jobs", "lease_expires_at")
    op.drop_column("transcription_jobs", "lease_owner")
    op.drop_column("transcription_jobs", "terminal_at")
    op.drop_column("transcription_jobs", "processing_started_at")
    op.drop_column("transcription_jobs", "queued_at")
    op.drop_column("transcription_jobs", "failure_code")
    op.drop_column("transcription_jobs", "result_object_key")
    op.drop_column("transcription_jobs", "media_object_key")
    op.alter_column(
        "transcription_jobs",
        "source_url_encrypted",
        existing_type=sa.LargeBinary(),
        nullable=False,
    )
