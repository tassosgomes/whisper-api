"""Resolve user-supplied media paths within the configured input directory."""

import os
import ipaddress
import socket
from pathlib import Path, PureWindowsPath
from urllib.parse import urlsplit


ALLOWED_EXTENSIONS = frozenset({".mp4", ".mkv", ".webm", ".mp3", ".wav", ".m4a"})


class InvalidSourceURL(ValueError):
    """The supplied source URL is malformed or resolves to a forbidden address."""


def validate_source_url_syntax(source_url: str):
    """Parse an HTTPS source URL without resolving or connecting to its host."""
    if not isinstance(source_url, str) or not source_url or len(source_url) > 8192:
        raise InvalidSourceURL("URL de origem inválida.")
    if any(ord(character) < 32 or ord(character) == 127 for character in source_url):
        raise InvalidSourceURL("URL de origem inválida.")

    try:
        parsed = urlsplit(source_url)
        port = parsed.port or 443
    except ValueError as exc:
        raise InvalidSourceURL("URL de origem inválida.") from exc

    if (
        parsed.scheme.casefold() != "https"
        or not parsed.hostname
        or parsed.username is not None
        or parsed.password is not None
        or parsed.fragment
        or not 1 <= port <= 65535
    ):
        raise InvalidSourceURL("URL de origem inválida.")
    try:
        hostname = parsed.hostname.encode("idna").decode("ascii").casefold()
    except UnicodeError as exc:
        raise InvalidSourceURL("URL de origem inválida.") from exc
    if not hostname or len(hostname) > 253:
        raise InvalidSourceURL("URL de origem inválida.")

    return parsed, hostname, port


def resolve_public_addresses(hostname: str, port: int) -> list[tuple[int, str]]:
    """Resolve every address and reject the host if any answer is non-public."""
    try:
        answers = socket.getaddrinfo(
            hostname, port, type=socket.SOCK_STREAM, proto=socket.IPPROTO_TCP
        )
    except OSError as exc:
        raise InvalidSourceURL("URL de origem inválida.") from exc
    addresses: list[tuple[int, str]] = []
    for family, _, _, _, sockaddr in answers:
        address = sockaddr[0]
        try:
            parsed_address = ipaddress.ip_address(address)
        except ValueError as exc:
            raise InvalidSourceURL("URL de origem inválida.") from exc
        if not parsed_address.is_global or parsed_address.is_multicast:
            raise InvalidSourceURL("URL de origem inválida.")
        candidate = (family, address)
        if candidate not in addresses:
            addresses.append(candidate)
    if not addresses:
        raise InvalidSourceURL("URL de origem inválida.")
    return addresses


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
