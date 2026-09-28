"""Small, engine-independent values used by the transcription pipeline."""

from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class Segment:
    start: float
    end: float
    text: str
