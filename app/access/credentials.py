"""Provision, authenticate, rotate, and revoke API credentials."""

from __future__ import annotations

import hashlib
import hmac
import secrets
from dataclasses import dataclass
from datetime import datetime, timezone
from uuid import UUID, uuid4

from sqlalchemy import select
from sqlalchemy.engine import Engine
from sqlalchemy.orm import sessionmaker

from app.access.models import Account, Credential


INITIAL_PERMISSIONS = ("transcriptions:create", "transcriptions:read")
KEY_PREFIX = "wsk_"


class CredentialNotFound(LookupError):
    """Raised when an operator refers to an unknown credential or account."""


@dataclass(frozen=True)
class AuthenticatedCredential:
    account_id: UUID
    credential_id: UUID
    permissions: tuple[str, ...]


class CredentialStore:
    """PostgreSQL repository for accounts and API key verifiers."""

    def __init__(self, engine: Engine):
        self._sessions = sessionmaker(bind=engine, expire_on_commit=False)

    def provision_account(self, account_name: str) -> tuple[dict, str]:
        account_id = uuid4()
        credential_id = uuid4()
        api_key = self._new_key(credential_id)
        with self._sessions.begin() as session:
            account = Account(id=account_id, name=account_name.strip())
            credential = Credential(
                id=credential_id,
                account_id=account_id,
                key_hash=self._key_hash(api_key),
                permissions=list(INITIAL_PERMISSIONS),
            )
            session.add_all([account, credential])
            session.flush()
            return self._metadata(account, credential), api_key

    def issue_for_account(self, account_id: UUID) -> tuple[dict, str]:
        credential_id = uuid4()
        api_key = self._new_key(credential_id)
        with self._sessions.begin() as session:
            account = session.get(Account, account_id)
            if account is None:
                raise CredentialNotFound("Conta não encontrada.")
            credential = Credential(
                id=credential_id,
                account_id=account_id,
                key_hash=self._key_hash(api_key),
                permissions=list(INITIAL_PERMISSIONS),
            )
            session.add(credential)
            session.flush()
            return self._metadata(account, credential), api_key

    def rotate(self, credential_id: UUID) -> tuple[dict, str]:
        api_key = self._new_key(credential_id)
        with self._sessions.begin() as session:
            credential = session.execute(
                select(Credential)
                .where(Credential.id == credential_id)
                .with_for_update()
            ).scalar_one_or_none()
            if credential is None:
                raise CredentialNotFound("Credencial não encontrada.")
            account = session.get(Account, credential.account_id)
            credential.key_hash = self._key_hash(api_key)
            credential.rotated_at = datetime.now(timezone.utc)
            credential.revoked_at = None
            session.flush()
            return self._metadata(account, credential), api_key

    def revoke(self, credential_id: UUID) -> dict:
        with self._sessions.begin() as session:
            credential = session.execute(
                select(Credential)
                .where(Credential.id == credential_id)
                .with_for_update()
            ).scalar_one_or_none()
            if credential is None:
                raise CredentialNotFound("Credencial não encontrada.")
            account = session.get(Account, credential.account_id)
            if credential.revoked_at is None:
                credential.revoked_at = datetime.now(timezone.utc)
            session.flush()
            return self._metadata(account, credential)

    def metadata(self, credential_id: UUID) -> dict:
        with self._sessions() as session:
            row = session.execute(
                select(Account, Credential)
                .join(Credential, Credential.account_id == Account.id)
                .where(Credential.id == credential_id)
            ).one_or_none()
            if row is None:
                raise CredentialNotFound("Credencial não encontrada.")
            account, credential = row
            return self._metadata(account, credential)

    def authenticate(self, api_key: str | None) -> AuthenticatedCredential | None:
        credential_id = self._credential_id_from_key(api_key)
        if credential_id is None or api_key is None:
            return None

        with self._sessions.begin() as session:
            credential = session.execute(
                select(Credential)
                .where(Credential.id == credential_id)
                .with_for_update()
            ).scalar_one_or_none()
            if (
                credential is None
                or credential.revoked_at is not None
                or not hmac.compare_digest(credential.key_hash, self._key_hash(api_key))
            ):
                return None
            credential.last_used_at = datetime.now(timezone.utc)
            return AuthenticatedCredential(
                account_id=credential.account_id,
                credential_id=credential.id,
                permissions=tuple(credential.permissions),
            )

    @staticmethod
    def _new_key(credential_id: UUID) -> str:
        return f"{KEY_PREFIX}{credential_id.hex}.{secrets.token_urlsafe(32)}"

    @staticmethod
    def _key_hash(api_key: str) -> str:
        return hashlib.sha256(api_key.encode("utf-8")).hexdigest()

    @staticmethod
    def _credential_id_from_key(api_key: str | None) -> UUID | None:
        if not api_key or len(api_key) > 128 or not api_key.startswith(KEY_PREFIX):
            return None
        public_id, separator, secret = api_key[len(KEY_PREFIX) :].partition(".")
        if not separator or len(public_id) != 32 or not secret:
            return None
        try:
            return UUID(hex=public_id)
        except ValueError:
            return None

    @staticmethod
    def _metadata(account: Account, credential: Credential) -> dict:
        return {
            "accountId": str(account.id),
            "accountName": account.name,
            "credentialId": str(credential.id),
            "createdAt": _isoformat(credential.created_at),
            "permissions": list(credential.permissions),
            "lastUsedAt": _isoformat(credential.last_used_at),
            "rotatedAt": _isoformat(credential.rotated_at),
            "revokedAt": _isoformat(credential.revoked_at),
        }


def _isoformat(value: datetime | None) -> str | None:
    if value is None:
        return None
    return value.astimezone(timezone.utc).isoformat().replace("+00:00", "Z")
