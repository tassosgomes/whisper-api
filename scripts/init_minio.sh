#!/bin/sh
set -eu

until mc alias set whisper "$MINIO_ENDPOINT" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1; do
  sleep 2
done

mc mb --ignore-existing "whisper/$S3_BUCKET"

# ADR-001: a remocao fisica por DeleteObject so vale com versionamento
# desabilitado e sem Object Lock. Object Lock so pode ser ativado na criacao
# do bucket, e este `mb` propositalmente omite `--with-lock`.
mc version suspend "whisper/$S3_BUCKET" >/dev/null 2>&1
version_info=$(mc version info "whisper/$S3_BUCKET" 2>&1) || {
  echo "init_minio: consulta de versionamento do bucket $S3_BUCKET indisponivel; falhando fechado (ADR-001)" >&2
  exit 1
}
if printf '%s\n' "$version_info" | grep -qi "versioning.*enabled"; then
  echo "init_minio: versionamento do bucket $S3_BUCKET deve permanecer desabilitado (ADR-001)" >&2
  exit 1
fi
lock_info=$(mc admin bucket info "whisper/$S3_BUCKET" 2>&1) || {
  echo "init_minio: consulta de Object Lock do bucket $S3_BUCKET indisponivel; falhando fechado (ADR-001)" >&2
  exit 1
}
if printf '%s\n' "$lock_info" | grep -qi "object.lock.*enabled"; then
  echo "init_minio: Object Lock do bucket $S3_BUCKET deve permanecer ausente (ADR-001)" >&2
  exit 1
fi
policy_file=/tmp/whisper-s3-policy.json
cat > "$policy_file" <<EOF
{
  "Version": "2012-10-17",
  "Statement": [
    {
      "Effect": "Allow",
      "Action": ["s3:GetBucketLocation", "s3:ListBucket", "s3:ListBucketMultipartUploads"],
      "Resource": ["arn:aws:s3:::$S3_BUCKET"]
    },
    {
      "Effect": "Allow",
      "Action": ["s3:AbortMultipartUpload", "s3:DeleteObject", "s3:GetObject", "s3:ListMultipartUploadParts", "s3:PutObject"],
      "Resource": ["arn:aws:s3:::$S3_BUCKET/*"]
    }
  ]
}
EOF

mc admin policy info whisper whisper-bucket-access >/dev/null 2>&1 \
  || mc admin policy create whisper whisper-bucket-access "$policy_file"
mc admin user info whisper "$S3_ACCESS_KEY_ID" >/dev/null 2>&1 \
  || mc admin user add whisper "$S3_ACCESS_KEY_ID" "$S3_SECRET_ACCESS_KEY"
mc admin policy attach whisper whisper-bucket-access --user "$S3_ACCESS_KEY_ID"
