"""Single-model, CPU-only faster-whisper adapter."""

from pathlib import Path
from time import monotonic

from .models import Segment


class Transcriber:
    def __init__(self, model_path: Path, model_name: str, cpu_threads: int) -> None:
        self.model_path = Path(model_path)
        self.model_name = model_name
        self.cpu_threads = cpu_threads
        if not self.model_path.is_dir():
            raise FileNotFoundError(f"Modelo local não encontrado: {self.model_path}")
        if cpu_threads < 1:
            raise ValueError("cpu_threads precisa ser maior que zero.")

        from faster_whisper import WhisperModel

        started = monotonic()
        self._model = WhisperModel(
            str(self.model_path),
            device="cpu",
            compute_type="int8",
            cpu_threads=cpu_threads,
        )
        self.model_load_seconds = monotonic() - started

    def transcribe(self, media_path: Path) -> tuple[list[Segment], float]:
        """Decode all segments before returning; decode errors propagate to caller."""
        raw_segments, info = self._model.transcribe(
            str(media_path),
            language="pt",
            beam_size=5,
            vad_filter=True,
            word_timestamps=False,
        )
        segments = [
            Segment(start=float(item.start), end=float(item.end), text=item.text)
            for item in raw_segments
        ]
        return segments, float(info.duration)
