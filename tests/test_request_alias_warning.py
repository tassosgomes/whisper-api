"""Public creation aliases stay sourceUrl and clientReference without Pydantic noise."""

from __future__ import annotations

import warnings

import pytest
from pydantic import ValidationError

from app.api.transcriptions import TranscriptionRequest


def _unsupported_alias_warnings(
    caught: list[warnings.WarningMessage],
) -> list[warnings.WarningMessage]:
    return [
        item
        for item in caught
        if item.category.__name__ == "UnsupportedFieldAttributeWarning"
    ]


def test_request_alias_warning_source_url() -> None:
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        TranscriptionRequest.model_rebuild(force=True)
        accepted = TranscriptionRequest.model_validate(
            {"sourceUrl": "https://media.example.test/signed/arquivo.mp4"}
        )
        schema = TranscriptionRequest.model_json_schema()

    assert accepted.source_url == "https://media.example.test/signed/arquivo.mp4"
    assert "sourceUrl" in schema["required"]
    assert "source_url" not in schema["properties"]
    assert schema["properties"]["sourceUrl"]["type"] == "string"
    with pytest.raises(ValidationError):
        TranscriptionRequest.model_validate({})
    assert _unsupported_alias_warnings(caught) == []


def test_request_alias_warning_client_reference() -> None:
    payload = {"sourceUrl": "https://media.example.test/signed/arquivo.mp4"}
    with warnings.catch_warnings(record=True) as caught:
        warnings.simplefilter("always")
        TranscriptionRequest.model_rebuild(force=True)
        omitted = TranscriptionRequest.model_validate(payload)
        present = TranscriptionRequest.model_validate(
            {**payload, "clientReference": "asset-7891"}
        )
        schema = TranscriptionRequest.model_json_schema()

    assert omitted.client_reference is None
    assert present.client_reference == "asset-7891"
    assert "clientReference" in schema["properties"]
    assert "clientReference" not in schema["required"]
    assert "client_reference" not in schema["properties"]
    assert _unsupported_alias_warnings(caught) == []
