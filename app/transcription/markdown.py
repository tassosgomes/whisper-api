"""Render a timestamped transcript and write it atomically."""

import os
import re
import tempfile
from pathlib import Path
from typing import Sequence

from .models import Segment


def format_timestamp(seconds: float) -> str:
    total = max(0, int(seconds))
    hours, remainder = divmod(total, 3600)
    minutes, seconds = divmod(remainder, 60)
    return f"{hours:02d}:{minutes:02d}:{seconds:02d}"


def _table_value(value: str) -> str:
    return value.replace("|", "\\|").replace("\n", " ").replace("\r", " ")


def render_markdown(
    source_name: str,
    segments: Sequence[Segment],
    *,
    duration_seconds: float,
    model_name: str,
    cpu_threads: int,
    elapsed_seconds: float,
) -> str:
    """Preserve segment order and speech while removing redundant whitespace."""
    speed = duration_seconds / elapsed_seconds if elapsed_seconds > 0 else 0.0
    lines = [
        f"# Transcrição — {source_name}",
        "",
        "## Informações",
        "",
        "| Informação | Valor |",
        "|---|---|",
        f"| Arquivo | {_table_value(source_name)} |",
        f"| Duração | {format_timestamp(duration_seconds)} |",
        "| Idioma | Português |",
        f"| Modelo | {_table_value(model_name)} |",
        "| Processamento | CPU / INT8 |",
        f"| CPUs disponíveis | {cpu_threads} |",
        f"| Tempo de transcrição | {format_timestamp(elapsed_seconds)} |",
        f"| Velocidade | {speed:.2f}× realtime |".replace(".", ","),
        "",
        "---",
        "",
        "## Transcrição",
    ]
    for segment in segments:
        text = re.sub(r"\s+", " ", segment.text).strip()
        lines.extend(("", f"**[{format_timestamp(segment.start)}]**", "", text))
    return "\n".join(lines) + "\n"


def write_markdown_atomic(
    destination: Path,
    source_name: str,
    segments: Sequence[Segment],
    *,
    duration_seconds: float,
    model_name: str,
    cpu_threads: int,
    elapsed_seconds: float,
) -> Path:
    """Write beside the destination and replace it only after a complete write."""
    destination = Path(destination)
    content = render_markdown(
        source_name,
        segments,
        duration_seconds=duration_seconds,
        model_name=model_name,
        cpu_threads=cpu_threads,
        elapsed_seconds=elapsed_seconds,
    )
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary_path: Path | None = None
    try:
        with tempfile.NamedTemporaryFile(
            mode="w",
            encoding="utf-8",
            newline="\n",
            dir=destination.parent,
            prefix=f".{destination.name}.",
            suffix=".tmp",
            delete=False,
        ) as temporary:
            temporary_path = Path(temporary.name)
            temporary.write(content)
            temporary.flush()
            os.fsync(temporary.fileno())
        os.replace(temporary_path, destination)
    finally:
        if temporary_path is not None:
            temporary_path.unlink(missing_ok=True)
    return destination
