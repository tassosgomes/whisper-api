"""FastAPI application and process-scoped model lifecycle."""

from __future__ import annotations

import os
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

from fastapi import FastAPI, HTTPException, Request, status
from fastapi.responses import JSONResponse

from app.access.credentials import CredentialStore
from app.api.transcriptions import router
from app.database import create_database_engine, database_url_from_env, upgrade_database
from app.jobs.executor import JobManager
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
        )
        if settings.WHISPER_DEVICE != "cpu" or settings.WHISPER_COMPUTE_TYPE != "int8":
            raise ValueError(
                "Esta PoC requer WHISPER_DEVICE=cpu e WHISPER_COMPUTE_TYPE=int8"
            )
        if settings.WHISPER_THREADS < 1 or settings.JOB_CONCURRENCY != 1:
            raise ValueError(
                "WHISPER_THREADS deve ser positivo e JOB_CONCURRENCY deve ser 1"
            )
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
        application.state.model_loaded = False
        application.state.jobs = None
        application.state.transcriber = Transcriber(
            model_path=settings.WHISPER_MODEL_PATH,
            model_name=settings.WHISPER_MODEL_NAME,
            cpu_threads=settings.WHISPER_THREADS,
        )
        application.state.jobs = JobManager()
        application.state.model_loaded = True
        yield
    finally:
        application.state.model_loaded = False
        jobs = getattr(application.state, "jobs", None)
        if jobs is not None:
            jobs.shutdown()
        database_engine.dispose()


app = FastAPI(title="Meeting Transcriber PoC", lifespan=lifespan)


@app.exception_handler(HTTPException)
async def http_exception_to_problem(
    request: Request, exc: HTTPException
) -> JSONResponse:
    """Render 401/404 as RFC 9457 problem+json per api-contract.yaml."""
    if exc.status_code == status.HTTP_401_UNAUTHORIZED:
        detail = exc.detail if isinstance(exc.detail, str) else "Credencial inválida."
        return JSONResponse(
            status_code=status.HTTP_401_UNAUTHORIZED,
            content={
                "type": "about:blank",
                "title": "Autenticação necessária",
                "status": status.HTTP_401_UNAUTHORIZED,
                "code": "UNAUTHORIZED",
                "detail": detail,
            },
            media_type="application/problem+json",
            headers=exc.headers,
        )
    if exc.status_code == status.HTTP_404_NOT_FOUND:
        detail = exc.detail if isinstance(exc.detail, str) else "Job não encontrado."
        return JSONResponse(
            status_code=status.HTTP_404_NOT_FOUND,
            content={
                "type": "about:blank",
                "title": "Recurso não encontrado",
                "status": status.HTTP_404_NOT_FOUND,
                "code": "NOT_FOUND",
                "detail": detail,
            },
            media_type="application/problem+json",
            headers=exc.headers,
        )
    return JSONResponse(
        status_code=exc.status_code,
        content={"detail": exc.detail},
        headers=exc.headers,
    )


app.include_router(router)
