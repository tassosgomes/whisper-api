"""Single-worker, in-memory transcription queue."""

from __future__ import annotations

import copy
import logging
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
from threading import Lock
from uuid import uuid4

from app.service import run_transcription


logger = logging.getLogger(__name__)


def _timestamp() -> str:
    return datetime.now(timezone.utc).isoformat().replace("+00:00", "Z")


class JobManager:
    """Keep job states in memory and run one transcription at a time."""

    def __init__(self) -> None:
        self._executor = ThreadPoolExecutor(
            max_workers=1, thread_name_prefix="transcription"
        )
        self._jobs: dict[str, dict] = {}
        self._lock = Lock()

    def submit(
        self,
        relative_path: str,
        transcriber: object,
        *,
        account_id: str,
        credential_id: str,
    ) -> dict:
        job_id = str(uuid4())
        job = {
            "id": job_id,
            "status": "queued",
            "input": relative_path,
            "createdAt": _timestamp(),
            "accountId": account_id,
            "credentialId": credential_id,
        }
        with self._lock:
            self._jobs[job_id] = job
            try:
                self._executor.submit(self._run, job_id, relative_path, transcriber)
            except Exception:
                del self._jobs[job_id]
                raise
            return self._public_job(job)

    def get(self, job_id: str, *, account_id: str, credential_id: str) -> dict | None:
        with self._lock:
            job = self._jobs.get(job_id)
            if (
                job is None
                or job["accountId"] != account_id
                or job["credentialId"] != credential_id
            ):
                return None
            return self._public_job(job)

    @staticmethod
    def _public_job(job: dict) -> dict:
        public = copy.deepcopy(job)
        public.pop("accountId", None)
        public.pop("credentialId", None)
        return public

    def shutdown(self) -> None:
        self._executor.shutdown(wait=True)

    def _run(self, job_id: str, relative_path: str, transcriber: object) -> None:
        with self._lock:
            self._jobs[job_id].update(status="processing", startedAt=_timestamp())

        try:
            result = run_transcription(relative_path, job_id, transcriber)
            completion = {
                "output": result["output"],
                "metricsOutput": result["metricsOutput"],
                "metrics": result["metrics"],
            }
        except Exception:
            logger.exception("Transcription job %s failed", job_id)
            with self._lock:
                self._jobs[job_id].update(
                    status="failed",
                    finishedAt=_timestamp(),
                    error={
                        "code": "TRANSCRIPTION_FAILED",
                        "message": "Não foi possível processar o arquivo informado.",
                    },
                )
            return

        with self._lock:
            self._jobs[job_id].update(
                status="completed",
                finishedAt=_timestamp(),
                **completion,
            )
