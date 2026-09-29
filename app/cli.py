"""Offline command line entry point using the same workflow as the API."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
from uuid import uuid4

from app.service import run_transcription
from app.transcription.whisper import Transcriber


def main() -> int:
    parser = argparse.ArgumentParser(description="Transcrição local de áudio ou vídeo")
    commands = parser.add_subparsers(dest="command", required=True)
    transcribe = commands.add_parser(
        "transcribe", help="Transcreve um arquivo de /data/input"
    )
    transcribe.add_argument("path", help="Caminho relativo a /data/input")
    args = parser.parse_args()

    model_path = Path(os.getenv("WHISPER_MODEL_PATH", "/models/medium"))
    model_name = os.getenv("WHISPER_MODEL_NAME", "medium")
    cpu_threads = int(os.getenv("WHISPER_THREADS", "8"))
    engine = Transcriber(model_path, model_name, cpu_threads)
    result = run_transcription(args.path, str(uuid4()), engine)
    print(json.dumps(result, ensure_ascii=False, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
