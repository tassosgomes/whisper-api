"""HTTP endpoints for asynchronous transcription jobs."""

from __future__ import annotations

from typing import Annotated

from fastapi import APIRouter, Depends, Header, HTTPException, Request, Response, status
from pydantic import BaseModel, ConfigDict, Field

from app.access.credentials import AuthenticatedCredential
from app.jobs.executor import IdempotencyKeyReused
from app.transcription.paths import InvalidSourceURL, validate_source_url_syntax
from app.transcription.source import MediaSizeExceeded, SourceUnavailable


router = APIRouter()


class TranscriptionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    source_url: Annotated[str, Field(alias="sourceUrl", min_length=1)]
    client_reference: Annotated[str | None, Field(alias="clientReference")] = None


def authenticate_api_key(
    request: Request,
    api_key: Annotated[str | None, Header(alias="X-API-Key")] = None,
) -> AuthenticatedCredential:
    credential = request.app.state.credential_store.authenticate(api_key)
    if credential is None:
        raise HTTPException(
            status_code=status.HTTP_401_UNAUTHORIZED,
            detail="Credencial inválida.",
            headers={"WWW-Authenticate": "APIKey"},
        )
    return credential


@router.post("/v1/transcriptions", status_code=status.HTTP_202_ACCEPTED)
def create_transcription(
    payload: TranscriptionRequest,
    request: Request,
    response: Response,
    credential: Annotated[AuthenticatedCredential, Depends(authenticate_api_key)],
    idempotency_key: Annotated[
        str, Header(alias="Idempotency-Key", min_length=1, max_length=256)
    ],
) -> dict:
    try:
        validate_source_url_syntax(payload.source_url)
        semantic_payload = payload.model_dump(
            mode="json", by_alias=True, exclude_unset=True
        )
        job = request.app.state.jobs.create_or_replay(
            semantic_payload,
            source_url=payload.source_url,
            client_reference=payload.client_reference,
            idempotency_key=idempotency_key,
            account_id=credential.account_id,
            credential_id=credential.credential_id,
        )
    except IdempotencyKeyReused:
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={
                "title": "Conflito de idempotência",
                "code": "IDEMPOTENCY_KEY_REUSED",
                "detail": "A chave já foi usada com outra solicitação.",
            },
        ) from None
    except InvalidSourceURL:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "title": "URL de origem inválida",
                "code": "INVALID_SOURCE_URL",
                "detail": "Informe uma URL HTTPS de origem válida.",
            },
        ) from None
    except MediaSizeExceeded:
        raise HTTPException(
            status_code=status.HTTP_413_REQUEST_ENTITY_TOO_LARGE,
            detail={
                "title": "Mídia acima do limite",
                "code": "MEDIA_SIZE_LIMIT_EXCEEDED",
                "detail": "A mídia excede o limite permitido de 5 GiB.",
            },
        ) from None
    except SourceUnavailable:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={
                "title": "Erro interno",
                "code": "INTERNAL_ERROR",
                "detail": "Não foi possível iniciar a conexão com a origem.",
            },
        ) from None

    response.headers["Location"] = job["statusUrl"]
    accepted = {
        "jobId": job["jobId"],
        "status": job["status"],
        "createdAt": job["createdAt"],
        "statusUrl": job["statusUrl"],
    }
    if "clientReference" in job:
        accepted["clientReference"] = job["clientReference"]
    return accepted


@router.get("/v1/transcriptions/{job_id}")
def get_transcription(
    job_id: str,
    request: Request,
    credential: Annotated[AuthenticatedCredential, Depends(authenticate_api_key)],
) -> dict:
    job = request.app.state.jobs.get(
        job_id,
        account_id=credential.account_id,
        credential_id=credential.credential_id,
    )
    if job is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="Job não encontrado."
        )
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
