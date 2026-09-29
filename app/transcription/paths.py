"""Resolve user-supplied media paths within the configured input directory."""

import os
from pathlib import Path, PureWindowsPath


ALLOWED_EXTENSIONS = frozenset({".mp4", ".mkv", ".webm", ".mp3", ".wav", ".m4a"})


def resolve_input_path(relative_path: str, input_dir: Path) -> Path:
    """Return an existing, readable media file inside ``input_dir``.

    Raises ValueError for invalid paths or unsupported media and
    FileNotFoundError when the requested file does not exist.
    """
    if not isinstance(relative_path, str) or not relative_path.strip():
        raise ValueError("O caminho do arquivo não pode estar vazio.")
    if "\x00" in relative_path:
        raise ValueError("O caminho do arquivo contém um caractere inválido.")

    path = Path(relative_path)
    windows_path = PureWindowsPath(relative_path)
    if path.is_absolute() or windows_path.drive or windows_path.root:
        raise ValueError("Informe um caminho relativo a data/input.")
    if "\\" in relative_path:
        raise ValueError("Use / como separador de diretórios.")

    root = input_dir.resolve(strict=True)
    candidate = (root / path).resolve(strict=False)
    if not candidate.is_relative_to(root):
        raise ValueError("O arquivo precisa estar dentro de data/input.")
    if candidate.suffix.lower() not in ALLOWED_EXTENSIONS:
        raise ValueError(
            f"Extensão de arquivo não permitida: {candidate.suffix or '(nenhuma)'}."
        )
    if not candidate.exists():
        raise FileNotFoundError(f"Arquivo não encontrado: {relative_path}")
    if not candidate.is_file():
        raise ValueError("O caminho precisa apontar para um arquivo regular.")
    if not os.access(candidate, os.R_OK):
        raise ValueError("O arquivo não pode ser lido.")
    return candidate
