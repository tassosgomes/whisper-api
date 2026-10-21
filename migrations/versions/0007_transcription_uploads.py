"""Persist direct-upload reservations independently from jobs.

Revision ID: 0007_transcription_uploads
Revises: 0006_administrative_audit
"""

from alembic import op
import sqlalchemy as sa


revision = "0007_transcription_uploads"
down_revision = "0006_administrative_audit"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.create_table(
        "transcription_uploads",
        sa.Column("id", sa.String(40), primary_key=True, nullable=False),
        sa.Column(
            "account_id",
            sa.Uuid(),
            sa.ForeignKey("accounts.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column(
            "credential_id",
            sa.Uuid(),
            sa.ForeignKey("credentials.id", ondelete="CASCADE"),
            nullable=False,
        ),
        sa.Column("idempotency_key_hash", sa.String(64), nullable=False),
        sa.Column("payload_fingerprint", sa.String(64), nullable=False),
        sa.Column("content_type", sa.String(64), nullable=False),
        sa.Column("size_bytes", sa.BigInteger(), nullable=False),
        sa.Column("checksum_sha256", sa.String(44), nullable=False),
        sa.Column("duration_ms", sa.BigInteger(), nullable=True),
        sa.Column("client_reference", sa.String(256), nullable=True),
        sa.Column("object_key", sa.String(512), nullable=False),
        sa.Column("status", sa.String(24), nullable=False),
        sa.Column("failure_code", sa.String(64), nullable=True),
        sa.Column(
            "job_id",
            sa.String(40),
            sa.ForeignKey("transcription_jobs.id", ondelete="CASCADE"),
            nullable=True,
        ),
        sa.Column(
            "created_at",
            sa.DateTime(timezone=True),
            server_default=sa.func.now(),
            nullable=False,
        ),
        sa.Column("updated_at", sa.DateTime(timezone=True), nullable=False),
        sa.Column("expires_at", sa.DateTime(timezone=True), nullable=False),
        sa.UniqueConstraint(
            "account_id",
            "credential_id",
            "idempotency_key_hash",
            name="uq_upload_scope_key",
        ),
    )
    op.create_index(
        "ix_upload_pending_scope",
        "transcription_uploads",
        ["credential_id", "status", "expires_at"],
    )


def downgrade() -> None:
    op.drop_index("ix_upload_pending_scope", table_name="transcription_uploads")
    op.drop_table("transcription_uploads")
