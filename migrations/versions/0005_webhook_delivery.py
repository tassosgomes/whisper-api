"""Persist account webhook configuration, signing keys, and terminal events.

Revision ID: 0005_webhook_delivery
Revises: 0004_download_not_before
Create Date: 2026-09-29
"""

from __future__ import annotations

from typing import Sequence

from alembic import op
import sqlalchemy as sa
from sqlalchemy.dialects import postgresql


revision: str = "0005_webhook_delivery"
down_revision: str | None = "0004_download_not_before"
branch_labels: str | Sequence[str] | None = None
depends_on: str | Sequence[str] | None = None


def upgrade() -> None:
    op.add_column("accounts", sa.Column("webhook_url", sa.Text(), nullable=True))
    op.add_column(
        "accounts",
        sa.Column("webhook_disabled_at", sa.DateTime(timezone=True), nullable=True),
    )
    op.create_table(
        "webhook_secrets",
        sa.Column(
            "id", postgresql.UUID(as_uuid=True), primary_key=True, nullable=False
        ),
        sa.Column(
            "account_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("accounts.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("secret_ciphertext", sa.LargeBinary(), nullable=False),
        sa.Column(
            "is_current", sa.Boolean(), nullable=False, server_default=sa.false()
        ),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            nullable=False,
            server_default=sa.func.now(),
        ),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=True),
    )
    op.create_index(
        "uq_webhook_secrets_current_account",
        "webhook_secrets",
        ["account_id"],
        unique=True,
        postgresql_where=sa.text("is_current"),
    )
    op.create_index(
        "ix_webhook_secrets_account_expires",
        "webhook_secrets",
        ["account_id", "expires_at"],
    )
    op.create_table(
        "notification_events",
        sa.Column("event_id", sa.String(length=64), primary_key=True, nullable=False),
        sa.Column("job_id", sa.String(length=40), nullable=False, unique=True),
        sa.Column(
            "account_id",
            postgresql.UUID(as_uuid=True),
            sa.ForeignKey("accounts.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("payload", sa.LargeBinary(), nullable=False),
        sa.Column("status", sa.String(length=24), nullable=False),
        sa.Column("attempt_count", sa.Integer(), nullable=False, server_default="0"),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            nullable=False,
            server_default=sa.func.now(),
        ),
        sa.Column("retry_deadline", sa.DateTime(timezone=True), nullable=False),
        sa.Column("next_attempt_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("last_attempt_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("last_timestamp", sa.Integer(), nullable=True),
        sa.Column("delivered_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("completed_at", sa.DateTime(timezone=True), nullable=True),
        sa.Column("last_status_code", sa.Integer(), nullable=True),
        sa.Column("last_error_code", sa.String(length=64), nullable=True),
        sa.Column("lease_owner", sa.String(length=64), nullable=True),
        sa.Column("lease_expires_at", sa.DateTime(timezone=True), nullable=True),
    )
    op.create_index(
        "ix_notification_events_due",
        "notification_events",
        ["status", "next_attempt_at"],
    )
    op.create_index(
        "ix_notification_events_account", "notification_events", ["account_id"]
    )


def downgrade() -> None:
    op.drop_index("ix_notification_events_account", table_name="notification_events")
    op.drop_index("ix_notification_events_due", table_name="notification_events")
    op.drop_table("notification_events")
    op.drop_index("ix_webhook_secrets_account_expires", table_name="webhook_secrets")
    op.drop_index("uq_webhook_secrets_current_account", table_name="webhook_secrets")
    op.drop_table("webhook_secrets")
    op.drop_column("accounts", "webhook_disabled_at")
    op.drop_column("accounts", "webhook_url")
