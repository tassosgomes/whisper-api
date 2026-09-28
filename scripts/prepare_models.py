"""Baixa modelos CTranslate2 usados pelo faster-whisper para ./models."""

from __future__ import annotations

import argparse
import os
from pathlib import Path

from faster_whisper.utils import download_model

AVAILABLE_MODELS = ("small", "medium", "large-v3")
REQUIRED_FILES = (
    "config.json",
    "model.bin",
    "tokenizer.json",
)


def model_is_ready(path: Path) -> bool:
    return all((path / filename).is_file() for filename in REQUIRED_FILES)


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Baixa snapshots compatíveis com faster-whisper para /models."
    )
    parser.add_argument(
        "models",
        nargs="*",
        choices=AVAILABLE_MODELS,
        help="Modelos a baixar (padrão: small medium large-v3).",
    )
    args = parser.parse_args()

    models_dir = Path(os.environ.get("MODELS_DIR", "/models"))
    models_dir.mkdir(parents=True, exist_ok=True)
    selected_models = args.models or AVAILABLE_MODELS

    for model_name in selected_models:
        destination = models_dir / model_name
        if model_is_ready(destination):
            print(f"Modelo {model_name} já está pronto em {destination}")
            continue

        print(f"Baixando {model_name} para {destination}...")
        download_model(model_name, output_dir=str(destination))
        if not model_is_ready(destination):
            raise RuntimeError(
                f"O snapshot de {model_name} está incompleto em {destination}"
            )
        print(f"Modelo {model_name} preparado em {destination}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
