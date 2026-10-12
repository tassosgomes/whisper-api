"""Decode a minimal media fixture through faster-whisper and PyAV 18."""

from __future__ import annotations

import wave
from pathlib import Path

from faster_whisper.audio import decode_audio


def test_decode_audio_fixture(tmp_path: Path) -> None:
    media_path = tmp_path / "minimal.wav"
    with wave.open(str(media_path), "wb") as media:
        media.setnchannels(1)
        media.setsampwidth(2)
        media.setframerate(16000)
        media.writeframes(b"\x00\x01" * 1600)

    decoded = decode_audio(str(media_path))

    assert decoded.dtype.name == "float32"
    assert decoded.shape == (1600,)
