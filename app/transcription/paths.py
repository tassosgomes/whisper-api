"""Resolve user-supplied media paths within the configured input directory."""

import os
import ipaddress
import socket
from dataclasses import dataclass
from pathlib import Path, PureWindowsPath
from urllib.parse import urlsplit


ALLOWED_EXTENSIONS = frozenset({".mp4", ".mkv", ".webm", ".mp3", ".wav", ".m4a"})


class InvalidSourceURL(ValueError):
    """The supplied source URL is malformed or resolves to a forbidden address."""


_DEFAULT_PORTS = {"https": 443, "http": 80}
_Network = ipaddress.IPv4Network | ipaddress.IPv6Network
_Address = ipaddress.IPv4Address | ipaddress.IPv6Address


@dataclass(frozen=True)
class _TrustedHost:
    scheme: str
    hostname: str
    port: int


@dataclass(frozen=True)
class _TrustedNetwork:
    scheme: str
    network: _Network


@dataclass(frozen=True)
class TrustedDestinations:
    """Explicit allowlist of private destinations (empty = strict default).

    Each entry is ``[http://]host[:port]`` or ``[http://]CIDR``. Entries
    without the ``http://`` prefix apply to HTTPS only. A host entry without
    a port means the default port of its scheme; CIDR entries match any port.
    """

    hosts: tuple[_TrustedHost, ...] = ()
    networks: tuple[_TrustedNetwork, ...] = ()

    @classmethod
    def parse(cls, value: str | None) -> "TrustedDestinations":
        hosts: list[_TrustedHost] = []
        networks: list[_TrustedNetwork] = []
        for raw_entry in (value or "").split(","):
            entry = raw_entry.strip()
            if not entry:
                continue
            scheme = "https"
            if entry.casefold().startswith("http://"):
                scheme, entry = "http", entry[7:]
            elif entry.casefold().startswith("https://"):
                entry = entry[8:]
            try:
                if "/" in entry:
                    networks.append(
                        _TrustedNetwork(scheme, ipaddress.ip_network(entry))
                    )
                    continue
                parsed = urlsplit(f"//{entry}")
                port = parsed.port or _DEFAULT_PORTS[scheme]
                if not parsed.hostname or parsed.username or parsed.password:
                    raise ValueError
                hostname = parsed.hostname.encode("idna").decode("ascii").casefold()
                if "*" in hostname or not 1 <= port <= 65535:
                    raise ValueError
            except ValueError:
                raise ValueError(
                    f"Entrada inválida na lista de destinos confiáveis: {raw_entry!r}."
                ) from None
            hosts.append(_TrustedHost(scheme, hostname, port))
        return cls(tuple(hosts), tuple(networks))

    @classmethod
    def from_env(cls, name: str) -> "TrustedDestinations":
        return cls.parse(os.getenv(name))

    def may_use_http(self, hostname: str, port: int) -> bool:
        """Cheap pre-resolution gate: could any entry authorize this HTTP URL?"""
        return any(n.scheme == "http" for n in self.networks) or self._host_match(
            "http", hostname, port
        )

    def _host_match(self, scheme: str, hostname: str, port: int) -> bool:
        return any(h == _TrustedHost(scheme, hostname, port) for h in self.hosts)

    def _covers(self, scheme: str, addresses: list[_Address]) -> bool:
        return all(
            any(n.scheme == scheme and a in n.network for n in self.networks)
            for a in addresses
        )

    def authorizes(
        self, scheme: str, hostname: str, port: int, addresses: list[_Address]
    ) -> bool:
        return self._host_match(scheme, hostname, port) or self._covers(
            scheme, addresses
        )


def _is_never_allowed(address: _Address) -> bool:
    """Loopback, link-local (incl. cloud metadata), multicast: never reachable."""
    return (
        address.is_loopback
        or address.is_link_local
        or address.is_multicast
        or address.is_unspecified
    )


def validate_source_url_syntax(
    source_url: str, trusted: TrustedDestinations | None = None
):
    """Parse a source/webhook URL without resolving or connecting to its host.

    HTTPS is always accepted; HTTP only when ``trusted`` may authorize it.
    """
    if not isinstance(source_url, str) or not source_url or len(source_url) > 8192:
        raise InvalidSourceURL("URL de origem inválida.")
    if any(ord(character) < 32 or ord(character) == 127 for character in source_url):
        raise InvalidSourceURL("URL de origem inválida.")

    try:
        parsed = urlsplit(source_url)
        scheme = parsed.scheme.casefold()
        port = parsed.port or _DEFAULT_PORTS.get(scheme, 0)
    except ValueError as exc:
        raise InvalidSourceURL("URL de origem inválida.") from exc

    if (
        scheme not in _DEFAULT_PORTS
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
    if scheme == "http" and not (trusted and trusted.may_use_http(hostname, port)):
        raise InvalidSourceURL("URL de origem inválida.")

    return parsed, hostname, port


def resolve_destination_addresses(
    hostname: str,
    port: int,
    scheme: str = "https",
    trusted: TrustedDestinations | None = None,
) -> list[tuple[int, str]]:
    """Resolve every address and apply the destination policy to all answers.

    Default: HTTPS and only global addresses. ``trusted`` can additionally
    authorize a private destination (matching host/port or CIDR covering every
    answer) and, via ``http://`` entries, plain HTTP. Loopback, link-local and
    multicast answers are rejected even when listed.
    """
    scheme = scheme.casefold()
    try:
        answers = socket.getaddrinfo(
            hostname, port, type=socket.SOCK_STREAM, proto=socket.IPPROTO_TCP
        )
    except OSError as exc:
        raise InvalidSourceURL("URL de origem inválida.") from exc
    addresses: list[tuple[int, str]] = []
    parsed_addresses: list[_Address] = []
    for family, _, _, _, sockaddr in answers:
        address = sockaddr[0]
        try:
            parsed_address = ipaddress.ip_address(address)
        except ValueError as exc:
            raise InvalidSourceURL("URL de origem inválida.") from exc
        effective = getattr(parsed_address, "ipv4_mapped", None) or parsed_address
        if _is_never_allowed(effective):
            raise InvalidSourceURL("URL de origem inválida.")
        parsed_addresses.append(effective)
        candidate = (family, address)
        if candidate not in addresses:
            addresses.append(candidate)
    if not addresses:
        raise InvalidSourceURL("URL de origem inválida.")
    all_global = all(a.is_global for a in parsed_addresses)
    if scheme == "https" and all_global:
        return addresses
    if trusted is not None and trusted.authorizes(
        scheme, hostname, port, parsed_addresses
    ):
        return addresses
    raise InvalidSourceURL("URL de origem inválida.")


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
