"""HTTP endpoints for transcription jobs."""

from pathlib import Path

from fastapi import APIRouter, HTTPException, Request, status
from pydantic import BaseModel, Field

from app.transcription.paths import resolve_input_path


router = APIRouter()


class TranscriptionRequest(BaseModel):
    path: str = Field(min_length=1)
    model: str | None = None


@router.post("/v1/transcriptions", status_code=status.HTTP_202_ACCEPTED)
def create_transcription(payload: TranscriptionRequest, request: Request) -> dict:
    settings = request.app.state.settings
    if payload.model is not None and payload.model != settings.WHISPER_MODEL_NAME:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail=f"O modelo carregado é {settings.WHISPER_MODEL_NAME}.",
        )

    try:
        resolve_input_path(payload.path, Path(settings.INPUT_DIR))
    except (ValueError, OSError) as exc:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail="Caminho de entrada inválido ou arquivo indisponível.",
        ) from exc

    job = request.app.state.jobs.submit(payload.path, request.app.state.transcriber)
    return {"id": job["id"], "status": job["status"], "input": job["input"]}


@router.get("/v1/transcriptions/{job_id}")
def get_transcription(job_id: str, request: Request) -> dict:
    job = request.app.state.jobs.get(job_id)
    if job is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Job não encontrado.")
    return job


@router.get("/health")
def health(request: Request) -> dict:
    settings = request.app.state.settings
    loaded = bool(getattr(request.app.state, "model_loaded", False))
    return {
        "status": "ok" if loaded else "starting",
        "model": settings.WHISPER_MODEL_NAME,
        "device": settings.WHISPER_DEVICE,
        "computeType": settings.WHISPER_COMPUTE_TYPE,
        "modelLoaded": loaded,
    }
