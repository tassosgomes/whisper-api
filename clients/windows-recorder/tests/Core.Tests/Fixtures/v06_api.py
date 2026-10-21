"""V-06: real API/auth/upload manager over HTTP, isolated DB and simulated S3.

Only infrastructure and time are replaced. No transcription worker is started:
processing/polling belongs to V-07. The private IPC protocol never logs secrets.
"""

from __future__ import annotations

from datetime import datetime, timedelta
import json
import logging
import os
from pathlib import Path
import socket
import sys
import threading
import time
import warnings
from uuid import uuid4

import uvicorn
from pydantic.warnings import UnsupportedFieldAttributeWarning
from moto.server import ThreadedMotoServer
from moto.s3.responses import S3Response
from sqlalchemy import URL, create_engine, select, text
from sqlalchemy.engine import make_url
from sqlalchemy.orm import Session

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))

from app.access.credentials import CredentialStore  # noqa: E402
from app.database import Base, create_database_engine  # noqa: E402
from app.jobs.executor import JobManager  # noqa: E402
from app.jobs.models import TranscriptionJob, TranscriptionUpload  # noqa: E402
from app.jobs.security import SourceURLCipher  # noqa: E402
from app.jobs.uploads import UploadManager  # noqa: E402
import app.jobs.uploads as upload_module  # noqa: E402
from app.main import app  # noqa: E402
from app.monitoring.metrics import DownloadStartMetrics  # noqa: E402
from app.storage.s3 import S3ObjectStore  # noqa: E402
from app.transcription.paths import TrustedDestinations  # noqa: E402
from app.transcription.source import SourceConnector  # noqa: E402


# Match the repository API tests: moto only remembers the signed SHA-256 header
# when an optional SDK header is present. Real S3 also accepts the header alone.
original_checksum = S3Response._get_checksum


def signed_checksum(response, response_headers):
    if response.headers.get("x-amz-checksum-sha256") and not response.headers.get(
        "x-amz-sdk-checksum-algorithm"
    ):
        checksum = response.headers["x-amz-checksum-sha256"]
        response_headers["x-amz-checksum-sha256"] = checksum
        return "SHA256", checksum
    return original_checksum(response, response_headers)


# FastAPI/Pydantic emit alias metadata warnings for the existing upload model.
# Keep this known warning out of fixture diagnostics; HTTP tests still assert
# all public aliases. Changing the Python API is outside this client task.
warnings.filterwarnings(
    "ignore",
    message="The 'alias' attribute.*",
    category=UnsupportedFieldAttributeWarning,
)
S3Response._get_checksum = signed_checksum
logging.getLogger("werkzeug").setLevel(logging.CRITICAL)
clock_offset = timedelta()


class ControlledDatetime(datetime):
    @classmethod
    def now(cls, tz=None):
        return datetime.now(tz) + clock_offset


upload_module.datetime = ControlledDatetime


def emit(value):
    print(json.dumps(value), flush=True)


def main():
    global clock_offset
    bucket = ThreadedMotoServer(ip_address="127.0.0.1", port=0, verbose=False)
    bucket.start()
    host, port = bucket.get_host_and_port()
    store = S3ObjectStore(
        bucket="whisper-v06-test",
        endpoint_url=f"http://{host}:{port}",
        access_key_id="test-access-key",
        secret_access_key="test-secret-key",
    )
    store.client.create_bucket(Bucket=store.bucket)
    # Upload timestamps and row locks require PostgreSQL, as in the API suite.
    # A random schema avoids touching any table belonging to another test run.
    url = (
        make_url(os.environ["TEST_DATABASE_URL"])
        if os.getenv("TEST_DATABASE_URL")
        else URL.create(
            "postgresql+psycopg",
            username=os.getenv("TEST_POSTGRES_USER", "whisper_test"),
            password=os.getenv("TEST_POSTGRES_PASSWORD", "local-test-only"),
            host=os.getenv("TEST_DATABASE_HOST", "127.0.0.1"),
            port=int(os.getenv("TEST_POSTGRES_PORT", "55432")),
            database=os.getenv("TEST_POSTGRES_DB", "whisper_test"),
        )
    )
    if (
        url.host not in {"127.0.0.1", "localhost", "::1"}
        or "test" not in (url.database or "").lower()
    ):
        raise RuntimeError("V06 requires a local isolated test database")
    control = create_database_engine(url.render_as_string(hide_password=False))
    schema = "v06_" + uuid4().hex
    with control.begin() as connection:
        connection.execute(text(f'CREATE SCHEMA "{schema}"'))
    engine = create_engine(url, connect_args={"options": f"-csearch_path={schema}"})
    try:
        Base.metadata.create_all(engine)
        credentials = CredentialStore(engine)
        cipher = SourceURLCipher.from_hex("1a" * 32)
        app.state.credential_store = credentials
        app.state.jobs = JobManager(
            engine,
            cipher,
            SourceConnector(TrustedDestinations.from_env("V06_TRUSTED_SOURCES")),
            DownloadStartMetrics(),
        )
        app.state.uploads = UploadManager(engine, cipher, store, pending_limit=1000)
        requests = []

        @app.middleware("http")
        async def record_request(request, call_next):
            body = None
            if request.url.path == "/v1/transcription-uploads":
                body = await request.json()
            response = await call_next(request)
            requests.append(
                {
                    "method": request.method,
                    "path": request.url.path,
                    "status": response.status_code,
                    "idempotencyKey": request.headers.get("Idempotency-Key"),
                    "body": body,
                }
            )
            return response

        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        server = uvicorn.Server(
            uvicorn.Config(app, lifespan="off", access_log=False, log_level="critical")
        )
        thread = threading.Thread(
            target=server.run, kwargs={"sockets": [listener]}, daemon=True
        )
        thread.start()
        try:
            deadline = time.monotonic() + 20
            while not server.started:
                if not thread.is_alive() or time.monotonic() > deadline:
                    raise RuntimeError("V06 API startup failed")
                time.sleep(0.01)
            emit(
                {
                    "endpoint": f"http://127.0.0.1:{listener.getsockname()[1]}",
                    "keys": {},
                }
            )
            for line in sys.stdin:
                command = json.loads(line)
                operation = command["operation"]
                if operation == "stop":
                    break
                if operation == "credential":
                    _, key = credentials.provision_account("V06 isolated test")
                    clock_offset = timedelta()
                    app.state.uploads.pending_limit = 1000
                    requests.clear()
                    emit({"key": key})
                elif operation == "advance":
                    clock_offset += timedelta(hours=command["hours"])
                    emit({"ok": True})
                elif operation == "limit":
                    app.state.uploads.pending_limit = command["value"]
                    emit({"ok": True})
                elif operation == "corrupt_checksum":
                    with Session(engine) as session:
                        record = session.get(TranscriptionUpload, command["uploadId"])
                        media = store.get_bytes(record.object_key)
                        store.client.put_object(
                            Bucket=store.bucket,
                            Key=record.object_key,
                            Body=media,
                            ContentType=record.content_type,
                            ChecksumSHA256="A" * 43 + "=",
                        )
                    emit({"ok": True})
                elif operation == "reject":
                    with Session(engine) as session, session.begin():
                        record = session.get(TranscriptionUpload, command["uploadId"])
                        record.status = "rejected"
                        record.failure_code = "UPLOAD_CHECKSUM_MISMATCH"
                    emit({"ok": True})
                elif operation == "evidence":
                    with Session(engine) as session:
                        uploads = session.scalars(
                            select(TranscriptionUpload).where(
                                TranscriptionUpload.client_reference
                                == command["reference"]
                            )
                        ).all()
                        jobs = session.scalars(
                            select(TranscriptionJob).where(
                                TranscriptionJob.client_reference
                                == command["reference"]
                            )
                        ).all()
                        emit(
                            {
                                "jobs": len(jobs),
                                "uploads": [
                                    {
                                        "uploadId": row.id,
                                        "jobId": row.job_id,
                                        "status": row.status,
                                    }
                                    for row in uploads
                                ],
                                "requests": requests,
                            }
                        )
                else:
                    raise ValueError("Unknown V06 command")
        finally:
            server.should_exit = True
            thread.join(timeout=10)
            listener.close()
            app.state.jobs.shutdown()
            engine.dispose()
    finally:
        engine.dispose()
        with control.begin() as connection:
            connection.execute(text(f'DROP SCHEMA "{schema}" CASCADE'))
        control.dispose()
        bucket.stop()


if __name__ == "__main__":
    main()
