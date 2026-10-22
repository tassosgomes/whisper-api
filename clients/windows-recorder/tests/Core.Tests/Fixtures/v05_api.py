"""Run the repository's real FastAPI app for the .NET V-05 tests.

Only the process lifecycle is replaced: an isolated SQLite database and the real
credential/job stores suffice for GET. No S3, worker, model, or external service
is needed. Requests still cross loopback HTTP and the production middleware,
authentication, getTranscription route and Problem Details handler.
The stdout/stdin JSON protocol is private IPC, never a diagnostic log.
"""

from __future__ import annotations

import json
from pathlib import Path
import socket
import sys
import tempfile
import threading
import time
from uuid import UUID

import uvicorn
from sqlalchemy import func, select

ROOT = Path(__file__).resolve().parents[5]
sys.path.insert(0, str(ROOT))

from app.access.credentials import CredentialStore  # noqa: E402
from app.database import Base, create_database_engine  # noqa: E402
from app.jobs.executor import JobManager  # noqa: E402
from app.jobs.models import TranscriptionJob  # noqa: E402
from app.jobs.security import SourceURLCipher  # noqa: E402
from app.main import app  # noqa: E402
from app.monitoring.metrics import DownloadStartMetrics  # noqa: E402
from app.transcription.paths import TrustedDestinations  # noqa: E402
from app.transcription.source import SourceConnector  # noqa: E402


def emit(value: dict) -> None:
    print(json.dumps(value), flush=True)


def main() -> None:
    with tempfile.TemporaryDirectory(prefix="whisper-v05-api-") as directory:
        engine = create_database_engine(f"sqlite:///{directory}/access.db")
        Base.metadata.create_all(engine)
        credentials = CredentialStore(engine)
        app.state.credential_store = credentials
        app.state.jobs = JobManager(
            engine,
            SourceURLCipher.from_hex("1a" * 32),
            SourceConnector(TrustedDestinations.from_env("V05_TRUSTED_SOURCES")),
            DownloadStartMetrics(),
        )
        keys = {}
        metadata = {}
        for name in ("valid", "replacement", "revoked", "inactive_account"):
            metadata[name], keys[name] = credentials.provision_account(f"V05 {name}")
        credentials.revoke(UUID(metadata["revoked"]["credentialId"]))
        # The current account model has no active flag or disable operation.
        # An account without active credentials is represented by revoking its
        # credentials through the production lifecycle, never by mocking auth.
        credentials.revoke(UUID(metadata["inactive_account"]["credentialId"]))

        requests = []

        @app.middleware("http")
        async def record_request(request, call_next):
            response = await call_next(request)
            requests.append(
                {
                    "method": request.method,
                    "path": request.url.path,
                    "status": response.status_code,
                }
            )
            return response

        listener = socket.socket()
        listener.bind(("127.0.0.1", 0))
        port = listener.getsockname()[1]
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
                    raise RuntimeError("V05 API startup failed")
                time.sleep(0.01)
            emit({"endpoint": f"http://127.0.0.1:{port}", "keys": keys})
            for line in sys.stdin:
                command = json.loads(line)
                if command["operation"] == "stop":
                    break
                if command["operation"] == "revoke":
                    credentials.revoke(UUID(metadata[command["name"]]["credentialId"]))
                    emit({"ok": True})
                elif command["operation"] == "evidence":
                    with engine.connect() as connection:
                        count = connection.scalar(
                            select(func.count()).select_from(TranscriptionJob)
                        )
                    emit({"jobs": count, "requests": requests})
                else:
                    raise ValueError("Unknown V05 command")
        finally:
            server.should_exit = True
            thread.join(timeout=10)
            listener.close()
            app.state.jobs.shutdown()
            engine.dispose()


if __name__ == "__main__":
    main()
