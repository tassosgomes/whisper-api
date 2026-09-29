"""S3-compatible storage for private transcription media and results."""

from __future__ import annotations

from pathlib import Path

import boto3
from botocore.config import Config


class S3ObjectStore:
    """Store private objects under separate media and result prefixes."""

    def __init__(
        self,
        *,
        bucket: str,
        region: str = "us-east-1",
        endpoint_url: str | None = None,
        access_key_id: str | None = None,
        secret_access_key: str | None = None,
    ) -> None:
        if not bucket:
            raise ValueError("S3_BUCKET precisa ser configurado.")
        self.bucket = bucket
        self.client = boto3.session.Session().client(
            "s3",
            region_name=region,
            endpoint_url=endpoint_url or None,
            aws_access_key_id=access_key_id or None,
            aws_secret_access_key=secret_access_key or None,
            config=Config(
                signature_version="s3v4",
                s3={"addressing_style": "path"},
                retries={"max_attempts": 3, "mode": "standard"},
            ),
        )

    @staticmethod
    def media_key(job_id: str, suffix: str = "") -> str:
        safe_suffix = suffix if suffix.startswith(".") and len(suffix) <= 10 else ""
        return f"media/{job_id}/source{safe_suffix}"

    @staticmethod
    def result_key(job_id: str) -> str:
        return f"results/{job_id}/result-v1.json"

    def upload_media(self, job_id: str, path: Path, *, suffix: str = "") -> str:
        key = self.media_key(job_id, suffix)
        self.client.upload_file(
            str(path),
            self.bucket,
            key,
            ExtraArgs={"ContentType": "application/octet-stream"},
        )
        return key

    def download_media(self, key: str, destination: Path) -> None:
        destination.parent.mkdir(parents=True, exist_ok=True)
        self.client.download_file(self.bucket, key, str(destination))

    def put_result(self, job_id: str, payload: bytes) -> str:
        key = self.result_key(job_id)
        self.client.put_object(
            Bucket=self.bucket,
            Key=key,
            Body=payload,
            ContentType="application/json",
        )
        return key

    def delete(self, key: str | None) -> None:
        if key:
            self.client.delete_object(Bucket=self.bucket, Key=key)

    def get_bytes(self, key: str) -> bytes:
        response = self.client.get_object(Bucket=self.bucket, Key=key)
        try:
            return response["Body"].read()
        finally:
            response["Body"].close()
