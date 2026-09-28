"""FastAPI application and process-scoped model lifecycle."""

from __future__ import annotations

import os
from contextlib import asynccontextmanager
from dataclasses import dataclass
from pathlib import Path

from fastapi import FastAPI

from app.api.transcriptions import router
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
        )
        if settings.WHISPER_DEVICE != "cpu" or settings.WHISPER_COMPUTE_TYPE != "int8":
            raise ValueError("Esta PoC requer WHISPER_DEVICE=cpu e WHISPER_COMPUTE_TYPE=int8")
        if settings.WHISPER_THREADS < 1 or settings.JOB_CONCURRENCY != 1:
            raise ValueError("WHISPER_THREADS deve ser positivo e JOB_CONCURRENCY deve ser 1")
        return settings


@asynccontextmanager
async def lifespan(application: FastAPI):
    settings = Settings.from_env()
    application.state.settings = settings
    application.state.model_loaded = False
    application.state.transcriber = Transcriber(
        model_path=settings.WHISPER_MODEL_PATH,
        model_name=settings.WHISPER_MODEL_NAME,
        cpu_threads=settings.WHISPER_THREADS,
    )
    application.state.jobs = JobManager()
    application.state.model_loaded = True
    try:
        yield
    finally:
        application.state.model_loaded = False
        application.state.jobs.shutdown()


app = FastAPI(title="Meeting Transcriber PoC", lifespan=lifespan)
app.include_router(router)
