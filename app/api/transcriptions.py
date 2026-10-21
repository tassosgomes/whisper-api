"""HTTP endpoints for asynchronous transcription jobs."""

from __future__ import annotations

import logging
import base64
import binascii
from typing import Annotated, Literal

from fastapi import APIRouter, Depends, Header, HTTPException, Request, Response, status
from pydantic import (
    BaseModel,
    ConfigDict,
    Field,
    ValidationError,
    field_validator,
    model_validator,
)

from app.access.credentials import AuthenticatedCredential
from app.jobs.executor import IdempotencyKeyReused
from app.jobs.uploads import (
    CONTENT_TYPE_SUFFIXES,
    TooManyPendingUploads,
    UploadRejected,
)
from app.transcription.paths import InvalidSourceURL, validate_source_url_syntax
from app.transcription.source import (
    ExpiredSourceURL,
    MediaSizeExceeded,
    SourceUnavailable,
    TransientSourceFailure,
    MAX_MEDIA_SIZE_BYTES,
)
from app.transcription.validation import MAX_MEDIA_DURATION_SECONDS


router = APIRouter()
logger = logging.getLogger(__name__)


class TranscriptionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", populate_by_name=True)

    source_url: Annotated[str, Field(alias="sourceUrl", min_length=1)]
    client_reference: Annotated[str | None, Field(alias="clientReference")] = None


class TranscriptionUploadRequest(BaseModel):
    model_config = ConfigDict(extra="forbid", strict=True)

    content_type: Annotated[str, Field(alias="contentType")]
    size_bytes: Annotated[int, Field(alias="sizeBytes", ge=1)]
    checksum_sha256: Annotated[
        str,
        Field(
            alias="checksumSha256",
            min_length=44,
            max_length=44,
            pattern=r"^[A-Za-z0-9+/]{43}=$",
        ),
    ]
    duration_ms: Annotated[int | None, Field(alias="durationMs", ge=1)] = None
    client_reference: Annotated[
        str | None, Field(alias="clientReference", max_length=256)
    ] = None

    @field_validator("checksum_sha256")
    @classmethod
    def validate_checksum(cls, value: str) -> str:
        try:
            digest = base64.b64decode(value, validate=True)
        except binascii.Error:
            raise ValueError("SHA-256 inválido.") from None
        if len(digest) != 32 or base64.b64encode(digest).decode() != value:
            raise ValueError("SHA-256 inválido.")
        return value

    @model_validator(mode="after")
    def reject_explicit_null(self):
        for field in ("duration_ms", "client_reference"):
            if field in self.model_fields_set and getattr(self, field) is None:
                raise ValueError("Campos opcionais não aceitam null.")
        return self


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


@router.post(
    "/v1/transcription-uploads",
    status_code=status.HTTP_201_CREATED,
    operation_id="createTranscriptionUpload",
)
def create_transcription_upload(
    payload: TranscriptionUploadRequest,
    request: Request,
    response: Response,
    credential: Annotated[AuthenticatedCredential, Depends(authenticate_api_key)],
    idempotency_key: Annotated[
        str, Header(alias="Idempotency-Key", min_length=1, max_length=128)
    ],
) -> dict:
    if payload.content_type not in CONTENT_TYPE_SUFFIXES:
        raise _upload_problem(
            422, "UNSUPPORTED_MEDIA_FORMAT", "Tipo de mídia não suportado."
        )
    if payload.size_bytes > MAX_MEDIA_SIZE_BYTES:
        raise _upload_problem(413, "MEDIA_SIZE_LIMIT_EXCEEDED", "A mídia excede 5 GiB.")
    if (
        payload.duration_ms is not None
        and payload.duration_ms > MAX_MEDIA_DURATION_SECONDS * 1000
    ):
        raise _upload_problem(
            422, "MEDIA_DURATION_LIMIT_EXCEEDED", "A mídia excede 2 horas."
        )
    try:
        body, created = request.app.state.uploads.create_or_replay(
            payload.model_dump(by_alias=True, exclude_none=True),
            idempotency_key=idempotency_key,
            account_id=credential.account_id,
            credential_id=credential.credential_id,
        )
    except IdempotencyKeyReused:
        raise _upload_problem(
            409, "IDEMPOTENCY_KEY_REUSED", "A chave já foi usada com outra solicitação."
        ) from None
    except UploadRejected:
        raise _upload_problem(
            409,
            "UPLOAD_REJECTED",
            "Este envio foi recusado. Use uma nova Idempotency-Key.",
        ) from None
    except TooManyPendingUploads:
        raise HTTPException(
            status_code=429,
            detail={
                "code": "TOO_MANY_PENDING_UPLOADS",
                "title": "Limite de envios pendentes",
                "detail": "Aguarde antes de pedir outro envio.",
            },
            headers={"Retry-After": "60"},
        ) from None
    response.status_code = 201 if created else 200
    if created:
        response.headers["Location"] = f"/v1/transcription-uploads/{body['uploadId']}"
    return body


def _upload_problem(status_code: int, code: str, detail: str) -> HTTPException:
    return HTTPException(
        status_code=status_code, detail={"code": code, "detail": detail}
    )


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
        validate_source_url_syntax(
            payload.source_url, request.app.state.source_connector.trusted
        )
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
