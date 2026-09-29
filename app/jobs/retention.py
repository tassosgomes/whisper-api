"""Shared retention rules for terminal transcription jobs."""

from __future__ import annotations

from datetime import datetime, timedelta, timezone


TERMINAL_RETENTION = timedelta(hours=24)
RETENTION_SCAN_INTERVAL_SECONDS = 60
TERMINAL_STATUSES = ("completed", "failed")


def is_terminal_expired(terminal_at: datetime | None, now: datetime) -> bool:
    if terminal_at is None:
        return False
    if terminal_at.tzinfo is None:
        terminal_at = terminal_at.replace(tzinfo=timezone.utc)
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)
    return terminal_at + TERMINAL_RETENTION <= now


def retention_deadline(terminal_at: datetime) -> datetime:
    """Physical-purge deadline for a terminal job (ADR-001: 24 h, not 24 h + scan)."""
    if terminal_at.tzinfo is None:
        terminal_at = terminal_at.replace(tzinfo=timezone.utc)
    return terminal_at + TERMINAL_RETENTION


def delay_until_next_scan(
    now: datetime, earliest_terminal_at: datetime | None
) -> float:
    """Seconds until the next retention sweep.

    The sweep must complete by each job's 24 h deadline, so when the next
    expiry falls inside the fixed scan interval the worker wakes at the
    expiry instead of sleeping the whole interval. Without terminal jobs
    the fixed interval applies.
    """
    if now.tzinfo is None:
        now = now.replace(tzinfo=timezone.utc)
    if earliest_terminal_at is None:
        return float(RETENTION_SCAN_INTERVAL_SECONDS)
    delay = (retention_deadline(earliest_terminal_at) - now).total_seconds()
    return max(0.0, min(float(RETENTION_SCAN_INTERVAL_SECONDS), delay))
