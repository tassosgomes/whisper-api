"""Durable Standard Webhooks delivery and operator-owned endpoint settings."""

from __future__ import annotations

import base64
import hashlib
import hmac
import http.client
import json
import logging
import math
import os
import random
import secrets
import socket
import ssl
import time
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone
from email.utils import parsedate_to_datetime
from threading import Event, Thread
from uuid import UUID, uuid4

from cryptography.exceptions import InvalidTag
from cryptography.hazmat.primitives.ciphers.aead import AESGCM
from sqlalchemy import delete, func, or_, select, update
from sqlalchemy.engine import Engine
from sqlalchemy.orm import Session, sessionmaker

from app.access.models import Account
from app.jobs.models import NotificationEvent, WebhookSecret
from app.monitoring.metrics import JobProcessingMetrics
from app.transcription.paths import (
    InvalidSourceURL,
    resolve_public_addresses,
    validate_source_url_syntax,
)


logger = logging.getLogger(__name__)
WEBHOOK_RETRY_WINDOW = timedelta(hours=72)
WEBHOOK_SECRET_OVERLAP = timedelta(hours=72)
WEBHOOK_CONNECT_TIMEOUT_SECONDS = 10
WEBHOOK_READ_TIMEOUT_SECONDS = 10
WEBHOOK_LEASE_SECONDS = 60
WEBHOOK_MAX_BACKOFF_SECONDS = 3600
WEBHOOK_RETRY_ADVISORY_LOCK = 4_377_739_212_784


class WebhookConfigurationError(ValueError):
    """An operator supplied invalid webhook configuration or key material."""


class WebhookTransportFailure(RuntimeError):
    """A network failure eligible for retry until the delivery deadline."""


class WebhookSecretCipher:
    """Encrypt webhook secrets at rest with a dedicated 256-bit AES-GCM key."""

    def __init__(self, key: bytes):
        if len(key) != 32:
            raise ValueError("WEBHOOK_SECRET_ENCRYPTION_KEY deve conter 32 bytes.")
        self._cipher = AESGCM(key)

    @classmethod
    def from_hex(cls, value: str | None) -> WebhookSecretCipher | None:
        if not value:
            return None
        if len(value) != 64:
            raise ValueError(
                "WEBHOOK_SECRET_ENCRYPTION_KEY deve conter 64 caracteres hexadecimais."
            )
        try:
            return cls(bytes.fromhex(value))
        except ValueError as exc:
            raise ValueError(
                "WEBHOOK_SECRET_ENCRYPTION_KEY deve conter 64 caracteres hexadecimais."
            ) from exc

    def encrypt(self, account_id: UUID, secret: str) -> bytes:
        nonce = os.urandom(12)
        associated_data = f"whisper-webhook-secret-v1:{account_id}".encode()
        return nonce + self._cipher.encrypt(
            nonce, secret.encode("ascii"), associated_data
        )

    def decrypt(self, account_id: UUID, ciphertext: bytes) -> str:
        nonce, encrypted = ciphertext[:12], ciphertext[12:]
        associated_data = f"whisper-webhook-secret-v1:{account_id}".encode()
        try:
            return self._cipher.decrypt(nonce, encrypted, associated_data).decode(
                "ascii"
            )
        except (InvalidTag, UnicodeDecodeError) as exc:
            raise WebhookConfigurationError("Material de webhook inválido.") from exc


class WebhookConfigStore:
    """Manage endpoint and signing-key configuration outside the public API."""

    def __init__(self, engine: Engine, cipher: WebhookSecretCipher | None):
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._cipher = cipher

    def configure(self, account_id: UUID, url: str) -> str:
        _validate_webhook_url(url)
        secret = generate_webhook_secret()
        cipher = self._require_cipher()
        with self._sessions.begin() as session:
            account = session.execute(
                select(Account).where(Account.id == account_id).with_for_update()
            ).scalar_one_or_none()
            if account is None:
                raise WebhookConfigurationError("Conta não encontrada.")
            current = session.scalar(
                select(WebhookSecret.id).where(
                    WebhookSecret.account_id == account_id,
                    WebhookSecret.is_current.is_(True),
                )
            )
            if current is not None:
                raise WebhookConfigurationError(
                    "O destino já tem material ativo; use rotação para trocá-lo."
                )
            account.webhook_url = url
            account.webhook_disabled_at = None
            session.add(
                WebhookSecret(
                    account_id=account_id,
                    secret_ciphertext=cipher.encrypt(account_id, secret),
                    is_current=True,
                )
            )
        return secret

    def set_endpoint(self, account_id: UUID, url: str) -> None:
        _validate_webhook_url(url)
        with self._sessions.begin() as session:
            account = session.execute(
                select(Account).where(Account.id == account_id).with_for_update()
            ).scalar_one_or_none()
            if account is None:
                raise WebhookConfigurationError("Conta não encontrada.")
            has_current_secret = session.scalar(
                select(WebhookSecret.id).where(
                    WebhookSecret.account_id == account_id,
                    WebhookSecret.is_current.is_(True),
                )
            )
            if has_current_secret is None:
                raise WebhookConfigurationError(
                    "Provisione o material de assinatura antes do destino."
                )
            account.webhook_url = url
            account.webhook_disabled_at = None

    def rotate(self, account_id: UUID, *, immediate: bool = False) -> str:
        secret = generate_webhook_secret()
        cipher = self._require_cipher()
        now = _utc_now()
        with self._sessions.begin() as session:
            account = session.execute(
                select(Account).where(Account.id == account_id).with_for_update()
            ).scalar_one_or_none()
            if account is None:
                raise WebhookConfigurationError("Conta não encontrada.")
            current = session.execute(
                select(WebhookSecret)
                .where(
                    WebhookSecret.account_id == account_id,
                    WebhookSecret.is_current.is_(True),
                )
                .with_for_update()
            ).scalar_one_or_none()
            if current is None:
                raise WebhookConfigurationError(
                    "A conta não tem material de webhook ativo."
                )
            current.is_current = False
            current.expires_at = now if immediate else now + WEBHOOK_SECRET_OVERLAP
            if immediate:
                session.execute(
                    update(WebhookSecret)
                    .where(
                        WebhookSecret.account_id == account_id,
                        WebhookSecret.is_current.is_(False),
                    )
                    .values(expires_at=now)
                )
            session.add(
                WebhookSecret(
                    account_id=account_id,
                    secret_ciphertext=cipher.encrypt(account_id, secret),
                    is_current=True,
                )
            )
        return secret

    def disable(self, account_id: UUID) -> None:
        now = _utc_now()
        with self._sessions.begin() as session:
            account = session.execute(
                select(Account).where(Account.id == account_id).with_for_update()
            ).scalar_one_or_none()
            if account is None:
                raise WebhookConfigurationError("Conta não encontrada.")
            account.webhook_disabled_at = now
            account.webhook_url = None
            session.execute(
                update(NotificationEvent)
                .where(
                    NotificationEvent.account_id == account_id,
                    NotificationEvent.status == "pending",
                )
                .values(
                    status="disabled",
                    completed_at=now,
                    next_attempt_at=None,
                    payload=b"",
                    last_error_code="operator_disabled",
                    lease_owner=None,
                    lease_expires_at=None,
                )
            )
            session.execute(
                delete(WebhookSecret).where(WebhookSecret.account_id == account_id)
            )

    def _require_cipher(self) -> WebhookSecretCipher:
        if self._cipher is None:
            raise WebhookConfigurationError(
                "WEBHOOK_SECRET_ENCRYPTION_KEY precisa ser configurada."
            )
        return self._cipher


@dataclass(frozen=True)
class WebhookHTTPResult:
    status_code: int
    retry_after_seconds: float | None = None


@dataclass(frozen=True)
class _DeliveryClaim:
    event_id: str
    account_id: UUID
    url: str
    payload: bytes
    timestamp: int
    retry_deadline: datetime
    owner: str
    secrets: tuple[bytes, ...]
    attempt_count: int


class _PinnedWebhookConnection(http.client.HTTPSConnection):
    """HTTPS connection pinned to an address that passed the SSRF check."""

    def __init__(self, hostname: str, port: int, address: str):
        super().__init__(hostname, port, timeout=WEBHOOK_CONNECT_TIMEOUT_SECONDS)
        self._approved_address = address

    def connect(self) -> None:
        sock = socket.create_connection(
            (self._approved_address, self.port), timeout=self.timeout
        )
        try:
            self.sock = self._context.wrap_socket(sock, server_hostname=self.host)
        except Exception:
            sock.close()
            raise


class WebhookHTTPTransport:
    """Post one signed body over pinned HTTPS without following redirects."""

    def send(self, url: str, body: bytes, headers: dict[str, str]) -> WebhookHTTPResult:
        parsed, hostname, port = _validate_webhook_url(url)
        addresses = resolve_public_addresses(hostname, port)
        family, address = addresses[0]
        if family not in {socket.AF_INET, socket.AF_INET6}:
            raise InvalidSourceURL("Destino de webhook inválido.")
        connection = _PinnedWebhookConnection(hostname, port, address)
        response = None
        try:
            connection.connect()
            if connection.sock is not None:
                connection.sock.settimeout(WEBHOOK_READ_TIMEOUT_SECONDS)
            target = parsed.path or "/"
            if parsed.query:
                target = f"{target}?{parsed.query}"
            connection.request("POST", target, body=body, headers=headers)
            response = connection.getresponse()
            return WebhookHTTPResult(
                response.status,
                _retry_after_seconds(response.getheader("Retry-After")),
            )
        except InvalidSourceURL:
            raise
        except (http.client.HTTPException, OSError, ssl.SSLError, ValueError):
            raise WebhookTransportFailure from None
        finally:
            if response is not None:
                response.close()
            connection.close()


class NotificationDispatcher:
    """Claim durable webhook events and retry them without mutating job state."""

    def __init__(
        self,
        engine: Engine,
        cipher: WebhookSecretCipher | None,
        metrics: JobProcessingMetrics,
        *,
        transport: WebhookHTTPTransport | None = None,
        clock=time.time,
        jitter=random.uniform,
    ):
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)
        self._cipher = cipher
        self._metrics = metrics
        self._transport = transport or WebhookHTTPTransport()
        self._clock = clock
        self._jitter = jitter
        self._stop = Event()
        self._thread: Thread | None = None

    def start(self) -> None:
        if self._thread is not None and self._thread.is_alive():
            return
        self._stop.clear()
        self._thread = Thread(
            target=self._delivery_loop, name="webhook-delivery", daemon=True
        )
        self._thread.start()

    def shutdown(self) -> None:
        self._stop.set()
        if self._thread is not None:
            self._thread.join(
                timeout=WEBHOOK_CONNECT_TIMEOUT_SECONDS
                + WEBHOOK_READ_TIMEOUT_SECONDS
                + 1
            )
        self._thread = None

    def run_once(self) -> bool:
        """Deliver one due event synchronously; useful to workers and focused tests."""
        claim = self._claim_next()
        if claim is None:
            return False
        now = _utc_now()
        try:
            if self._cipher is None:
                raise WebhookConfigurationError(
                    "WEBHOOK_SECRET_ENCRYPTION_KEY não está configurada."
                )
            signatures = []
            signed_content = (
                f"{claim.event_id}.{claim.timestamp}.".encode("ascii") + claim.payload
            )
            for encrypted_secret in claim.secrets:
                encoded_secret = self._cipher.decrypt(
                    claim.account_id, encrypted_secret
                )
                secret_bytes = _decode_webhook_secret(encoded_secret)
                signature = hmac.new(
                    secret_bytes, signed_content, hashlib.sha256
                ).digest()
                signatures.append(f"v1,{base64.b64encode(signature).decode('ascii')}")
            result = self._transport.send(
                claim.url,
                claim.payload,
                {
                    "Content-Type": "application/json",
                    "webhook-id": claim.event_id,
                    "webhook-timestamp": str(claim.timestamp),
                    "webhook-signature": " ".join(signatures),
                },
            )
        except InvalidSourceURL:
            self._finish_claim(
                claim, now, status="failed", error_code="invalid_destination"
            )
            self._metrics.observe_notification_attempt("failed")
            return True
        except WebhookConfigurationError:
            self._finish_claim(
                claim, now, status="failed", error_code="invalid_configuration"
            )
            self._metrics.observe_notification_attempt("failed")
            return True
        except WebhookTransportFailure:
            self._schedule_retry(claim, now, retry_after_seconds=None, status_code=None)
            return True

        status_code = result.status_code
        if 200 <= status_code < 300:
            self._finish_claim(
                claim,
                now,
                status="delivered",
                status_code=status_code,
            )
            self._metrics.observe_notification_attempt("delivered")
        elif status_code == 410:
            self._finish_claim(
                claim,
                now,
                status="disabled",
                status_code=status_code,
                error_code="endpoint_gone",
                disable_account=True,
            )
            self._metrics.observe_notification_attempt("disabled")
        elif status_code == 429 or status_code == 408 or status_code >= 500:
            self._schedule_retry(
                claim,
                now,
                retry_after_seconds=result.retry_after_seconds,
                status_code=status_code,
            )
        else:
            # Includes every 3xx. The transport never follows Location.
            self._finish_claim(
                claim,
                now,
                status="failed",
                status_code=status_code,
                error_code=(
                    "redirect_rejected" if 300 <= status_code < 400 else "http_error"
                ),
            )
            self._metrics.observe_notification_attempt("failed")
        return True

    def _delivery_loop(self) -> None:
        while not self._stop.is_set():
            try:
                if self.run_once():
                    continue
            except Exception as exc:
                logger.warning(
                    "Webhook delivery cycle deferred error_type=%s",
                    type(exc).__name__,
                )
            self._stop.wait(0.5)

    def _claim_next(self) -> _DeliveryClaim | None:
        now = _utc_now()
        owner = uuid4().hex
        with self._sessions.begin() as session:
            expired = session.execute(
                update(NotificationEvent)
                .where(
                    NotificationEvent.status == "pending",
                    NotificationEvent.retry_deadline <= now,
                )
                .values(
                    status="exhausted",
                    completed_at=now,
                    next_attempt_at=None,
                    payload=b"",
                    last_error_code="retry_window_expired",
                    lease_owner=None,
                    lease_expires_at=None,
                )
            )
            for _ in range(expired.rowcount or 0):
                self._metrics.observe_notification_attempt("exhausted")
            session.execute(
                delete(WebhookSecret).where(
                    WebhookSecret.is_current.is_(False),
                    WebhookSecret.expires_at <= now,
                )
            )
            session.execute(
                delete(NotificationEvent).where(
                    NotificationEvent.status.in_(
                        (
                            "delivered",
                            "disabled",
                            "failed",
                            "exhausted",
                            "not_configured",
                        )
                    ),
                    NotificationEvent.retry_deadline <= now - WEBHOOK_RETRY_WINDOW,
                )
            )
            session.execute(
                select(func.pg_advisory_xact_lock(WEBHOOK_RETRY_ADVISORY_LOCK))
            )
            event = session.execute(
                select(NotificationEvent)
                .where(
                    NotificationEvent.status == "pending",
                    NotificationEvent.next_attempt_at <= now,
                    NotificationEvent.retry_deadline > now,
                    or_(
                        NotificationEvent.lease_expires_at.is_(None),
                        NotificationEvent.lease_expires_at <= now,
                    ),
                )
                .order_by(
                    NotificationEvent.next_attempt_at, NotificationEvent.created_at
                )
                .limit(1)
                .with_for_update(skip_locked=True)
            ).scalar_one_or_none()
            if event is None:
                return None
            account = session.get(Account, event.account_id)
            if (
                account is None
                or account.webhook_url is None
                or account.webhook_disabled_at is not None
            ):
                event.status = (
                    "disabled"
                    if account and account.webhook_disabled_at
                    else "not_configured"
                )
                event.completed_at = now
                event.next_attempt_at = None
                event.payload = b""
                self._metrics.observe_notification_attempt(event.status)
                return None
            secret_rows = session.scalars(
                select(WebhookSecret)
                .where(
                    WebhookSecret.account_id == event.account_id,
                    or_(
                        WebhookSecret.is_current.is_(True),
                        WebhookSecret.expires_at > now,
                    ),
                )
                .order_by(
                    WebhookSecret.is_current.desc(), WebhookSecret.created_at.desc()
                )
            ).all()
            if not secret_rows:
                event.status = "failed"
                event.completed_at = now
                event.next_attempt_at = None
                event.payload = b""
                event.last_error_code = "missing_signing_secret"
                self._metrics.observe_notification_attempt("failed")
                return None
            timestamp = max(int(self._clock()), (event.last_timestamp or 0) + 1)
            event.last_timestamp = timestamp
            event.last_attempt_at = now
            event.attempt_count += 1
            event.lease_owner = owner
            event.lease_expires_at = now + timedelta(seconds=WEBHOOK_LEASE_SECONDS)
            return _DeliveryClaim(
                event_id=event.event_id,
                account_id=event.account_id,
                url=account.webhook_url,
                payload=event.payload,
                timestamp=timestamp,
                retry_deadline=event.retry_deadline,
                owner=owner,
                secrets=tuple(row.secret_ciphertext for row in secret_rows),
                attempt_count=event.attempt_count,
            )

    def _schedule_retry(
        self,
        claim: _DeliveryClaim,
        now: datetime,
        *,
        retry_after_seconds: float | None,
        status_code: int | None,
    ) -> None:
        upper_bound = min(
            WEBHOOK_MAX_BACKOFF_SECONDS,
            min(
                WEBHOOK_MAX_BACKOFF_SECONDS,
                2 ** min(max(claim.attempt_count - 1, 0), 12),
            ),
        )
        jitter = max(0.0, float(self._jitter(0.0, float(upper_bound))))
        retry_after = max(0.0, retry_after_seconds or 0.0)
        delay = max(jitter, retry_after)
        next_attempt = now + timedelta(seconds=delay)
        if next_attempt >= claim.retry_deadline:
            self._finish_claim(
                claim,
                now,
                status="exhausted",
                status_code=status_code,
                error_code="retry_window_expired",
            )
            self._metrics.observe_notification_attempt("exhausted")
            return
        self._finish_claim(
            claim,
            now,
            status="pending",
            status_code=status_code,
            error_code="transient_delivery_failure",
            next_attempt_at=next_attempt,
        )
        self._metrics.observe_notification_attempt("retry", retry_delay_seconds=delay)

    def _finish_claim(
        self,
        claim: _DeliveryClaim,
        now: datetime,
        *,
        status: str,
        status_code: int | None = None,
        error_code: str | None = None,
        next_attempt_at: datetime | None = None,
        disable_account: bool = False,
    ) -> None:
        with self._sessions.begin() as session:
            event = session.execute(
                select(NotificationEvent)
                .where(
                    NotificationEvent.event_id == claim.event_id,
                    NotificationEvent.lease_owner == claim.owner,
                )
                .with_for_update()
            ).scalar_one_or_none()
            if event is None:
                return
            event.status = status
            event.last_status_code = status_code
            event.last_error_code = error_code
            event.next_attempt_at = next_attempt_at
            if status != "pending":
                event.payload = b""
            event.lease_owner = None
            event.lease_expires_at = None
            if status == "delivered":
                event.delivered_at = now
                event.completed_at = now
            elif status != "pending":
                event.completed_at = now
            if disable_account:
                account = session.get(Account, event.account_id)
                if account is not None:
                    account.webhook_disabled_at = now
                    session.execute(
                        update(NotificationEvent)
                        .where(
                            NotificationEvent.account_id == event.account_id,
                            NotificationEvent.status == "pending",
                        )
                        .values(
                            status="disabled",
                            completed_at=now,
                            next_attempt_at=None,
                            payload=b"",
                            last_error_code="endpoint_gone",
                            lease_owner=None,
                            lease_expires_at=None,
                        )
                    )


def persist_terminal_event(session: Session, job, now: datetime) -> NotificationEvent:
    """Insert the immutable minimum payload in the job's terminal transaction."""
    payload = {
        "eventId": f"evt_{uuid4().hex}",
        "jobId": job.id,
        "status": job.status,
    }
    if job.client_reference is not None:
        payload["clientReference"] = job.client_reference
    event_id = payload["eventId"]
    raw_body = json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode(
        "utf-8"
    )
    account = session.get(Account, job.account_id)
    is_configured = bool(
        account is not None
        and account.webhook_url
        and account.webhook_disabled_at is None
    )
    event = NotificationEvent(
        event_id=event_id,
        job_id=job.id,
        account_id=job.account_id,
        payload=raw_body if is_configured else b"",
        status="pending" if is_configured else "not_configured",
        attempt_count=0,
        created_at=now,
        retry_deadline=now + WEBHOOK_RETRY_WINDOW,
        next_attempt_at=now if is_configured else None,
    )
    session.add(event)
    return event


def generate_webhook_secret() -> str:
    """Generate a Standard Webhooks ``whsec_`` secret with 32 random bytes."""
    return "whsec_" + base64.b64encode(secrets.token_bytes(32)).decode("ascii")


def _decode_webhook_secret(secret: str) -> bytes:
    if not secret.startswith("whsec_"):
        raise WebhookConfigurationError("Material de webhook inválido.")
    try:
        raw = base64.b64decode(secret[6:], validate=True)
    except (ValueError, base64.binascii.Error) as exc:
        raise WebhookConfigurationError("Material de webhook inválido.") from exc
    if not 24 <= len(raw) <= 64:
        raise WebhookConfigurationError("Material de webhook inválido.")
    return raw


def _validate_webhook_url(url: str):
    try:
        parsed, hostname, port = validate_source_url_syntax(url)
    except InvalidSourceURL as exc:
        raise WebhookConfigurationError(
            "O destino do webhook precisa ser uma URL HTTPS válida."
        ) from exc
    try:
        resolve_public_addresses(hostname, port)
    except InvalidSourceURL as exc:
        raise WebhookConfigurationError(
            "O destino do webhook precisa resolver somente para endereços públicos."
        ) from exc
    return parsed, hostname, port


def _retry_after_seconds(value: str | None) -> float | None:
    if value is None:
        return None
    try:
        seconds = float(value)
        return max(0.0, seconds) if math.isfinite(seconds) else None
    except ValueError:
        try:
            retry_at = parsedate_to_datetime(value)
        except (TypeError, ValueError, OverflowError):
            return None
        if retry_at.tzinfo is None:
            retry_at = retry_at.replace(tzinfo=timezone.utc)
        return max(0.0, (retry_at - datetime.now(timezone.utc)).total_seconds())


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)
