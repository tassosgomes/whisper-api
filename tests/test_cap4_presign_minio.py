"""Opt-in smoke: real S3 enforcement of the signed PUT constraints."""

import base64
import hashlib
import os
from uuid import uuid4

import httpx
import pytest

from app.storage.s3 import S3ObjectStore


MEDIA = b"RIFF-cap4-minio-smoke"
CHECKSUM = base64.b64encode(hashlib.sha256(MEDIA).digest()).decode()


@pytest.fixture
def minio_upload():
    endpoint = os.getenv("S3_SMOKE_ENDPOINT")
    if not endpoint:
        pytest.skip("S3_SMOKE_ENDPOINT não configurado")
    store = S3ObjectStore(
        bucket=os.getenv("S3_SMOKE_BUCKET", "whisper-temporary"),
        endpoint_url=endpoint,
        public_endpoint_url=endpoint,
        access_key_id=os.getenv("S3_SMOKE_ACCESS_KEY_ID", "whisper-local-root"),
        secret_access_key=os.getenv(
            "S3_SMOKE_SECRET_ACCESS_KEY", "local-minio-root-placeholder"
        ),
    )
    key = store.upload_key(f"upl_smoke_{uuid4().hex}", ".wav")
    target = store.presign_upload(
        key,
        content_type="audio/wav",
        size_bytes=len(MEDIA),
        checksum_sha256=CHECKSUM,
        expires_in=60,
    )
    try:
        yield store, key, target
    finally:
        store.delete(key)


def test_cap4_presign_minio_accepts_exact_headers(minio_upload):
    store, key, target = minio_upload
    response = httpx.put(
        target["url"], headers=target["headers"], content=MEDIA, trust_env=False
    )
    assert response.status_code == 200, response.text
    head = store.client.head_object(
        Bucket=store.bucket, Key=key, ChecksumMode="ENABLED"
    )
    assert head["ContentLength"] == len(MEDIA)
    assert head["ChecksumSHA256"] == CHECKSUM


def test_cap4_presign_minio_rejects_changed_size_or_checksum(minio_upload):
    store, key, target = minio_upload
    for changes, media in (
        ({"Content-Length": str(len(MEDIA) + 1)}, MEDIA + b"x"),
        ({"x-amz-checksum-sha256": base64.b64encode(b"x" * 32).decode()}, MEDIA),
        ({"Content-Type": "audio/mpeg"}, MEDIA),
        # Keep the signed checksum header, but send different bytes of equal size.
        ({}, b"x" * len(MEDIA)),
    ):
        response = httpx.put(
            target["url"],
            headers={**target["headers"], **changes},
            content=media,
            trust_env=False,
        )
        assert response.status_code in {400, 403}, response.text
    assert not list(store.list_keys(key))
