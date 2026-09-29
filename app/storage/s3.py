"""S3-compatible storage for private transcription media and results."""

from __future__ import annotations

from pathlib import Path
from typing import Iterator

import boto3
from botocore.config import Config
from botocore.exceptions import ClientError


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
        """Remove an object and every retained version of it (ADR-001).

        With versioning enabled a plain DeleteObject only writes a delete
        marker and preserves earlier versions. The bucket default stays
        versioning-off (see scripts/init_minio.sh), but the purge removes
        versions explicitly so it holds even if versioning was enabled.
        """
        if not key:
            return
        targets = self._version_targets(key)
        if targets:
            for index in range(0, len(targets), 1000):
                chunk = targets[index : index + 1000]
                self.client.delete_objects(
                    Bucket=self.bucket,
                    Delete={
                        "Objects": [
                            {"Key": item_key, "VersionId": version_id}
                            for item_key, version_id in chunk
                        ],
                        "Quiet": True,
                    },
                )
            return
        self.client.delete_object(Bucket=self.bucket, Key=key)

    def _version_targets(self, key: str) -> list[tuple[str, str]]:
        """All (key, version-id) entries for an exact key, or []."""
        try:
            paginator = self.client.get_paginator("list_object_versions")
            targets: list[tuple[str, str]] = []
            for page in paginator.paginate(Bucket=self.bucket, Prefix=key):
                for field in ("Versions", "DeleteMarkers"):
                    for item in page.get(field, []):
                        if item.get("Key") == key and item.get("VersionId"):
                            targets.append((key, item["VersionId"]))
            return targets
        except ClientError as exc:
            code = exc.response.get("Error", {}).get("Code", "")
            if code in {
                "NoSuchBucket",
                "NotImplemented",
                "InvalidRequest",
                "XNotImplemented",
                "MethodNotAllowed",
            }:
                return []
            raise

    def bucket_retention_policy(self) -> dict:
        """Effective bucket policy governing physical purges (ADR-001)."""
        versioning = self.client.get_bucket_versioning(Bucket=self.bucket).get("Status")
        try:
            configuration = self.client.get_object_lock_configuration(
                Bucket=self.bucket
            )
            object_lock = (
                configuration.get("ObjectLockConfiguration", {}).get(
                    "ObjectLockEnabled"
                )
                == "Enabled"
            )
        except ClientError as exc:
            code = exc.response.get("Error", {}).get("Code", "")
            if code in {
                "NoSuchObjectLockConfiguration",
                "ObjectLockConfigurationNotFoundError",
                "ObjectLockConfigurationNotFound",
                "NotImplemented",
                "XNotImplemented",
                "InvalidRequest",
                "NoSuchBucket",
                "MethodNotAllowed",
            }:
                object_lock = False
            else:
                raise
        return {"versioning": versioning, "object_lock": object_lock}

    def ensure_retention_compliant(self) -> dict:
        """Fail closed unless plain deletes physically remove objects.

        ADR-001 requires versioning disabled (never Enabled) and no Object
        Lock on the temporary bucket, so DeleteObject cannot leave versions
        or immutable copies behind.
        """
        policy = self.bucket_retention_policy()
        if policy["versioning"] == "Enabled":
            raise ValueError(
                "Bucket com versionamento ativo viola ADR-001: "
                "versionamento deve permanecer desabilitado."
            )
        if policy["object_lock"]:
            raise ValueError(
                "Bucket com Object Lock viola ADR-001: "
                "retenção imutável incompatível com expurgo de 24 h."
            )
        return policy

    def list_keys(self, prefix: str) -> Iterator[str]:
        paginator = self.client.get_paginator("list_objects_v2")
        for page in paginator.paginate(Bucket=self.bucket, Prefix=prefix):
            for item in page.get("Contents", []):
                key = item.get("Key")
                if key is not None:
                    yield key

    def get_bytes(self, key: str) -> bytes:
        response = self.client.get_object(Bucket=self.bucket, Key=key)
        try:
            return response["Body"].read()
        finally:
            response["Body"].close()
