"""Generate the pinned Kiota client without changing the approved OpenAPI contract.

Requires Python 3.12+ and PyYAML==6.0.3 (already in the repository dev dependencies).
"""

from __future__ import annotations

import json
from pathlib import Path
import subprocess

import yaml


CLIENT = Path(__file__).resolve().parents[1]
CONTRACT = (
    CLIENT.parents[1] / "tasks/prd-gravador-de-reunioes-desktop/api-contract.yaml"
)


def use_int64_for_unformatted_integers(node: object) -> None:
    # OpenAPI integers have no implicit int32 limit, but Kiota defaults to int32.
    # In particular sizeBytes accepts 5 GiB. Use int64 on the generation input,
    # keeping names, constraints, paths and the approved source file unchanged.
    if isinstance(node, dict):
        if node.get("type") == "integer" and "format" not in node:
            node["format"] = "int64"
        for value in node.values():
            use_int64_for_unformatted_integers(value)
    elif isinstance(node, list):
        for value in node:
            use_int64_for_unformatted_integers(value)


def main() -> None:
    document = yaml.safe_load(CONTRACT.read_text(encoding="utf-8"))
    use_int64_for_unformatted_integers(document)
    prepared = CLIENT / "obj/openapi-generation/api-contract.json"
    prepared.parent.mkdir(parents=True, exist_ok=True)
    prepared.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    subprocess.run(["dotnet", "tool", "restore"], cwd=CLIENT, check=True)
    subprocess.run(
        [
            "dotnet",
            "kiota",
            "generate",
            "--language",
            "CSharp",
            "--class-name",
            "WhisperApiClient",
            "--namespace-name",
            "Whisper.Recorder.Core.Api.Generated",
            "--openapi",
            "obj/openapi-generation/api-contract.json",
            "--output",
            "src/Core/Api/Generated",
            "--clean-output",
        ],
        cwd=CLIENT,
        check=True,
    )


if __name__ == "__main__":
    main()
