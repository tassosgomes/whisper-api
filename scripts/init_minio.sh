#!/bin/sh
set -eu

until mc alias set whisper "$MINIO_ENDPOINT" "$MINIO_ROOT_USER" "$MINIO_ROOT_PASSWORD" >/dev/null 2>&1; do
  sleep 2
done

mc mb --ignore-existing "whisper/$S3_BUCKET"
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
