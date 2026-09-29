"""Schedule the first download retry after a transient admission failure.

Revision ID: 0004_download_not_before
Revises: 0003_media_processing
Create Date: 2026-09-29
"""

from __future__ import annotations

from typing import Sequence

from alembic import op
import sqlalchemy as sa


revision: str = "0004_download_not_before"
down_revision: str | None = "0003_media_processing"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column(
        "transcription_jobs",
        sa.Column("download_not_before", sa.DateTime(timezone=True)),
    )


def downgrade() -> None:
    op.drop_column("transcription_jobs", "download_not_before")
