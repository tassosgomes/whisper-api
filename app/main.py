"""FastAPI application and process-scoped model lifecycle."""

from __future__ import annotations

import os
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.exceptions import RequestValidationError
from fastapi.responses import JSONResponse

from app.access.credentials import CredentialStore
from app.api.transcriptions import router
from app.database import create_database_engine, database_url_from_env, upgrade_database
from app.jobs.executor import JobManager
from app.jobs.security import SourceURLCipher
from app.jobs.worker import JobWorker
from app.monitoring.metrics import DownloadStartMetrics, JobProcessingMetrics
from app.storage.s3 import S3ObjectStore
from app.transcription.source import SourceConnector
from app.transcription.whisper import Transcriber


@dataclass(frozen=True)
class Settings:
    INPUT_DIR: str
    WHISPER_MODEL_PATH: Path
    WHISPER_MODEL_NAME: str
    WHISPER_DEVICE: str
    WHISPER_COMPUTE_TYPE: str
    WHISPER_THREADS: int
    JOB_CONCURRENCY: int
    DATABASE_URL: str
    SOURCE_URL_ENCRYPTION_KEY: str
    ROLE: str
    S3_BUCKET: str
    S3_REGION: str
    S3_ENDPOINT_URL: str | None
    S3_ACCESS_KEY_ID: str | None
    S3_SECRET_ACCESS_KEY: str | None

    @classmethod
    def from_env(cls) -> "Settings":
        settings = cls(
            INPUT_DIR=os.getenv("APP_INPUT_DIR", "/data/input"),
            WHISPER_MODEL_PATH=Path(os.getenv("WHISPER_MODEL_PATH", "/models/medium")),
            WHISPER_MODEL_NAME=os.getenv("WHISPER_MODEL_NAME", "medium"),
            WHISPER_DEVICE=os.getenv("WHISPER_DEVICE", "cpu"),
            WHISPER_COMPUTE_TYPE=os.getenv("WHISPER_COMPUTE_TYPE", "int8"),
            WHISPER_THREADS=int(os.getenv("WHISPER_THREADS", "8")),
            JOB_CONCURRENCY=int(os.getenv("JOB_CONCURRENCY", "1")),
            DATABASE_URL=database_url_from_env(),
            SOURCE_URL_ENCRYPTION_KEY=os.getenv("SOURCE_URL_ENCRYPTION_KEY", ""),
            ROLE=os.getenv("APP_ROLE", "api").casefold(),
            S3_BUCKET=os.getenv("S3_BUCKET", "whisper-temporary"),
            S3_REGION=os.getenv("S3_REGION", "us-east-1"),
            S3_ENDPOINT_URL=os.getenv("S3_ENDPOINT_URL") or None,
            S3_ACCESS_KEY_ID=os.getenv("S3_ACCESS_KEY_ID") or None,
            S3_SECRET_ACCESS_KEY=os.getenv("S3_SECRET_ACCESS_KEY") or None,
        )
        if settings.WHISPER_DEVICE != "cpu" or settings.WHISPER_COMPUTE_TYPE != "int8":
            raise ValueError(
                "Esta PoC requer WHISPER_DEVICE=cpu e WHISPER_COMPUTE_TYPE=int8"
            )
        if settings.WHISPER_THREADS < 1 or settings.JOB_CONCURRENCY != 1:
            raise ValueError(
                "WHISPER_THREADS deve ser positivo e JOB_CONCURRENCY deve ser 1"
            )
        if settings.ROLE not in {"api", "worker", "all"}:
            raise ValueError("APP_ROLE deve ser api, worker ou all.")
        return settings


@asynccontextmanager
async def lifespan(application: FastAPI):
    settings = Settings.from_env()
    application.state.settings = settings
    database_engine = create_database_engine(settings.DATABASE_URL)
    try:
        upgrade_database(database_engine)
        application.state.database_engine = database_engine
        application.state.credential_store = CredentialStore(database_engine)
        source_url_cipher = SourceURLCipher.from_hex(settings.SOURCE_URL_ENCRYPTION_KEY)
        application.state.source_url_cipher = source_url_cipher
        application.state.download_metrics = DownloadStartMetrics()
        application.state.job_metrics = JobProcessingMetrics()
        application.state.model_loaded = False
        application.state.jobs = None
        application.state.source_connector = SourceConnector()
        application.state.object_store = S3ObjectStore(
            bucket=settings.S3_BUCKET,
            region=settings.S3_REGION,
            endpoint_url=settings.S3_ENDPOINT_URL,
            access_key_id=settings.S3_ACCESS_KEY_ID,
            secret_access_key=settings.S3_SECRET_ACCESS_KEY,
        )
        # ADR-001: valida a política efetiva do bucket usado pela aplicação
        # em runtime e falha fechada se versionamento Enabled ou Object Lock
        # ativo (aplica-se aos papéis api, worker e all).
        application.state.object_store.ensure_retention_compliant()
        application.state.jobs = JobManager(
            database_engine,
            source_url_cipher,
            application.state.source_connector,
            application.state.download_metrics,
        )
        application.state.worker = None
        if settings.ROLE in {"worker", "all"}:
            application.state.transcriber = Transcriber(
                model_path=settings.WHISPER_MODEL_PATH,
                model_name=settings.WHISPER_MODEL_NAME,
                cpu_threads=settings.WHISPER_THREADS,
            )
            application.state.worker = JobWorker(
                database_engine,
                source_url_cipher,
                application.state.source_connector,
                application.state.object_store,
                application.state.transcriber,
                application.state.job_metrics,
            )
            application.state.worker.start()
            application.state.model_loaded = True
        yield
    finally:
        application.state.model_loaded = False
        worker = getattr(application.state, "worker", None)
        if worker is not None:
            worker.shutdown()
        jobs = getattr(application.state, "jobs", None)
        if jobs is not None:
            jobs.shutdown()
        database_engine.dispose()


app = FastAPI(title="Meeting Transcriber PoC", lifespan=lifespan)


@app.exception_handler(HTTPException)
async def http_exception_to_problem(
    request: Request, exc: HTTPException
) -> JSONResponse:
    """Render contract errors as safe RFC 9457 Problem Details."""
    defaults = {
        status.HTTP_400_BAD_REQUEST: (
            "Solicitação inválida",
            "INVALID_REQUEST",
            "A solicitação não pôde ser processada.",
        ),
        status.HTTP_401_UNAUTHORIZED: (
            "Autenticação necessária",
            "UNAUTHORIZED",
            "Credencial inválida.",
        ),
        status.HTTP_404_NOT_FOUND: (
            "Recurso não encontrado",
            "NOT_FOUND",
            "Job não encontrado.",
        ),
        status.HTTP_409_CONFLICT: (
            "Conflito",
            "CONFLICT",
            "A solicitação conflita com o estado atual.",
        ),
        status.HTTP_413_REQUEST_ENTITY_TOO_LARGE: (
            "Mídia acima do limite",
            "MEDIA_SIZE_LIMIT_EXCEEDED",
            "A mídia excede o limite permitido.",
        ),
        status.HTTP_422_UNPROCESSABLE_ENTITY: (
            "Solicitação inválida",
            "INVALID_REQUEST",
            "A solicitação não pôde ser processada.",
        ),
        status.HTTP_500_INTERNAL_SERVER_ERROR: (
            "Erro interno",
            "INTERNAL_ERROR",
            "A solicitação não pôde ser concluída.",
        ),
    }
    title, code, detail = defaults.get(
        exc.status_code, ("Erro HTTP", "HTTP_ERROR", "A solicitação falhou.")
    )
    if isinstance(exc.detail, dict):
        title = exc.detail.get("title", title)
        code = exc.detail.get("code", code)
        detail = exc.detail.get("detail", detail)
    elif exc.status_code not in {
        status.HTTP_401_UNAUTHORIZED,
        status.HTTP_404_NOT_FOUND,
    } and isinstance(exc.detail, str):
        detail = exc.detail
    problem = {
        "type": "about:blank",
        "title": title,
        "status": exc.status_code,
        "code": code,
        "detail": detail,
    }
    if exc.status_code != status.HTTP_404_NOT_FOUND:
        problem["instance"] = request.url.path
    return JSONResponse(
        status_code=exc.status_code,
        content=problem,
        media_type="application/problem+json",
        headers=exc.headers,
    )


@app.exception_handler(RequestValidationError)
async def request_validation_to_problem(
    request: Request, exc: RequestValidationError
) -> JSONResponse:
    """Hide Pydantic input values, which may include signed source URLs."""
    return JSONResponse(
        status_code=status.HTTP_400_BAD_REQUEST,
        content={
            "type": "about:blank",
            "title": "Solicitação inválida",
            "status": status.HTTP_400_BAD_REQUEST,
            "code": "INVALID_REQUEST",
            "detail": "O corpo ou os parâmetros obrigatórios são inválidos.",
            "instance": request.url.path,
        },
        media_type="application/problem+json",
    )


app.include_router(router)


@app.get("/metrics")
def metrics(request: Request) -> dict:
    """Expose low-cardinality job timing, failure, and resource aggregates."""
    return {
        "downloadStart": request.app.state.download_metrics.snapshot(),
        "jobs": request.app.state.job_metrics.snapshot(),
    }
