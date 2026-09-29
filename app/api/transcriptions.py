"""HTTP endpoints for asynchronous transcription jobs."""

from __future__ import annotations

import logging
from typing import Annotated, Literal

from fastapi import APIRouter, Depends, Header, HTTPException, Request, Response, status
from pydantic import BaseModel, ConfigDict, Field, ValidationError

from app.access.credentials import AuthenticatedCredential
from app.jobs.executor import IdempotencyKeyReused
from app.transcription.paths import InvalidSourceURL, validate_source_url_syntax
from app.transcription.source import (
    ExpiredSourceURL,
    MediaSizeExceeded,
    SourceUnavailable,
    TransientSourceFailure,
)


router = APIRouter()
logger = logging.getLogger(__name__)


class TranscriptionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    source_url: Annotated[str, Field(alias="sourceUrl", min_length=1)]
    client_reference: Annotated[str | None, Field(alias="clientReference")] = None


class TranscriptionResultSegment(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    start_ms: Annotated[int, Field(alias="startMs", ge=0)]
    end_ms: Annotated[int, Field(alias="endMs", ge=0)]
    text: str


class TranscriptionResult(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    schema_version: Literal[1] = Field(alias="schemaVersion")
    job_id: Annotated[str, Field(alias="jobId", min_length=1)]
    language: Literal["pt-BR"]
    duration_ms: Annotated[int, Field(alias="durationMs", ge=0)]
    segments: list[TranscriptionResultSegment]


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
    except (InvalidSourceURL, ExpiredSourceURL):
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "title": "URL de origem inválida",
                "code": "INVALID_SOURCE_URL",
                "detail": "Informe uma URL HTTPS de origem válida e não expirada.",
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
    except TransientSourceFailure:
        raise HTTPException(
            status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
            detail={
                "title": "Erro interno",
                "code": "INTERNAL_ERROR",
                "detail": "Não foi possível iniciar a conexão com a origem.",
            },
        ) from None
    except SourceUnavailable:
        raise HTTPException(
            status_code=status.HTTP_422_UNPROCESSABLE_ENTITY,
            detail={
                "title": "URL de origem inválida",
                "code": "INVALID_SOURCE_URL",
                "detail": "A origem rejeitou a URL de forma permanente.",
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


@router.get("/v1/transcriptions/{job_id}/result", response_model=TranscriptionResult)
def get_transcription_result(
    job_id: str,
    request: Request,
    credential: Annotated[AuthenticatedCredential, Depends(authenticate_api_key)],
) -> TranscriptionResult:
    reference = request.app.state.jobs.get_result_reference(
        job_id,
        account_id=credential.account_id,
        credential_id=credential.credential_id,
    )
    if reference is None:
        raise HTTPException(
            status_code=status.HTTP_404_NOT_FOUND, detail="Job não encontrado."
        )
    if reference["status"] != "completed":
        raise HTTPException(
            status_code=status.HTTP_409_CONFLICT,
            detail={
                "title": "Resultado indisponível",
                "code": "RESULT_NOT_AVAILABLE",
                "detail": "O resultado ainda não está disponível.",
            },
        )

    result_key = reference["resultObjectKey"]
    if not result_key:
        raise _result_read_error()
    try:
        result = TranscriptionResult.model_validate_json(
            request.app.state.object_store.get_bytes(result_key)
        )
        if result.job_id != job_id:
            raise ValueError("O objeto não corresponde ao job solicitado.")
    except (ValidationError, ValueError):
        raise _result_read_error() from None
    except Exception as exc:
        logger.warning(
            "Could not read transcription result error_type=%s", type(exc).__name__
        )
        raise _result_read_error() from None
    return result


def _result_read_error() -> HTTPException:
    return HTTPException(
        status_code=status.HTTP_500_INTERNAL_SERVER_ERROR,
        detail={
            "title": "Erro interno",
            "code": "INTERNAL_ERROR",
            "detail": "Não foi possível recuperar o resultado.",
        },
    )


@router.get("/health")
def health(request: Request) -> dict:
    settings = request.app.state.settings
    loaded = bool(getattr(request.app.state, "model_loaded", False))
    ready = settings.ROLE == "api" or loaded
    return {
        "status": "ok" if ready else "starting",
        "role": settings.ROLE,
        "model": settings.WHISPER_MODEL_NAME,
        "device": settings.WHISPER_DEVICE,
        "computeType": settings.WHISPER_COMPUTE_TYPE,
        "modelLoaded": loaded,
    }
