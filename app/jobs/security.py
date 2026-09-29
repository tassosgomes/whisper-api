"""Authenticated encryption for URLs that must survive process restarts."""

from __future__ import annotations

import hashlib
import hmac
import os

from cryptography.hazmat.primitives.ciphers.aead import AESGCM


class SourceURLCipher:
    """Encrypt stored source URLs and key request fingerprints by a secret key."""

    def __init__(self, key: bytes):
        if len(key) != 32:
            raise ValueError("SOURCE_URL_ENCRYPTION_KEY deve conter 32 bytes em hex.")
        self._cipher = AESGCM(key)
        self._fingerprint_key = hashlib.sha256(b"fingerprint\0" + key).digest()

    @classmethod
    def from_hex(cls, value: str | None) -> "SourceURLCipher":
        if not value:
            raise ValueError("SOURCE_URL_ENCRYPTION_KEY precisa ser configurada.")
        if len(value) != 64:
            raise ValueError(
                "SOURCE_URL_ENCRYPTION_KEY deve conter 64 caracteres hexadecimais."
            )
        try:
            key = bytes.fromhex(value)
        except ValueError as exc:
            raise ValueError(
                "SOURCE_URL_ENCRYPTION_KEY deve conter 64 caracteres hexadecimais."
            ) from exc
        return cls(key)

    @staticmethod
    def _associated_data(job_id: str) -> bytes:
        return f"whisper-source-url-v1:{job_id}".encode("utf-8")

    def encrypt(self, job_id: str, source_url: str) -> bytes:
        nonce = os.urandom(12)
        encrypted = self._cipher.encrypt(
            nonce, source_url.encode("utf-8"), self._associated_data(job_id)
        )
        return nonce + encrypted

    def decrypt(self, job_id: str, encrypted_source_url: bytes) -> str:
        nonce, ciphertext = encrypted_source_url[:12], encrypted_source_url[12:]
        return self._cipher.decrypt(
            nonce, ciphertext, self._associated_data(job_id)
        ).decode("utf-8")

    def idempotency_key_hash(self, idempotency_key: str) -> str:
        return hmac.new(
            self._fingerprint_key,
            b"idempotency-key\0" + idempotency_key.encode("utf-8"),
            hashlib.sha256,
        ).hexdigest()

    def payload_fingerprint(self, payload: dict) -> str:
        import json

        canonical = json.dumps(
            payload, sort_keys=True, separators=(",", ":"), ensure_ascii=False
        ).encode("utf-8")
        return hmac.new(
            self._fingerprint_key, b"payload\0" + canonical, hashlib.sha256
        ).hexdigest()
