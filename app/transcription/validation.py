"""Validate downloaded media containers, codecs, and duration before inference."""

from __future__ import annotations

from pathlib import Path

import av


MAX_MEDIA_DURATION_SECONDS = 2 * 60 * 60


class UnsupportedMediaFormat(ValueError):
    """The file is not an approved and decodable audio/video media format."""


class MediaDurationExceeded(ValueError):
    """The media exceeds the two hour processing limit."""


_EXPECTED_FORMATS = {
    ".mp4": {"mov,mp4,m4a,3gp,3g2,mj2"},
    ".mkv": {"matroska,webm"},
    ".webm": {"matroska,webm"},
    ".mp3": {"mp3"},
    ".wav": {"wav"},
    ".m4a": {"mov,mp4,m4a,3gp,3g2,mj2"},
}


def inspect_media(path: Path) -> float:
    """Return media duration in seconds after probing and decoding an audio frame."""
    expected_formats = _EXPECTED_FORMATS.get(path.suffix.casefold())
    if expected_formats is None:
        raise UnsupportedMediaFormat
    try:
        with av.open(str(path)) as container:
            if container.format.name not in expected_formats:
                raise UnsupportedMediaFormat
            audio_streams = [
                stream for stream in container.streams if stream.type == "audio"
            ]
            if not audio_streams:
                raise UnsupportedMediaFormat
            duration = _duration_seconds(container, audio_streams)
            if duration is None or duration < 0:
                raise UnsupportedMediaFormat
            if duration > MAX_MEDIA_DURATION_SECONDS:
                raise MediaDurationExceeded
            if next(container.decode(audio_streams[0]), None) is None:
                raise UnsupportedMediaFormat
            return duration
    except (UnsupportedMediaFormat, MediaDurationExceeded):
        raise
    except (av.error.FFmpegError, OSError, ValueError) as exc:
        raise UnsupportedMediaFormat from exc


def _duration_seconds(
    container: av.container.InputContainer, audio_streams
) -> float | None:
    if container.duration is not None:
        return float(container.duration) / av.time_base
    durations = [
        float(stream.duration * stream.time_base)
        for stream in audio_streams
        if stream.duration is not None and stream.time_base is not None
    ]
    return max(durations, default=None)
