"""Operator-only credential commands; API keys are printed only on issue/rotation."""

from __future__ import annotations

import argparse
import json
import os
import sys
from uuid import UUID

from app.access.credentials import CredentialNotFound, CredentialStore
from app.database import create_database_engine, database_url_from_env, upgrade_database
from app.jobs.webhooks import (
    WebhookConfigStore,
    WebhookSecretCipher,
)


def _credential_id(value: str) -> UUID:
    try:
        return UUID(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("credential_id deve ser UUID válido") from exc


def _account_id(value: str) -> UUID:
    try:
        return UUID(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError("account_id deve ser UUID válido") from exc


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Operação local de contas, API Keys e webhooks"
    )
    commands = parser.add_subparsers(dest="command", required=True)

    provision = commands.add_parser(
        "provision", help="Cria uma conta e sua primeira API Key"
    )
    provision.add_argument("--account-name", required=True)

    issue = commands.add_parser(
        "issue", help="Cria outra API Key para uma conta existente"
    )
    issue.add_argument("--account-id", required=True, type=_account_id)

    rotate = commands.add_parser(
        "rotate", help="Troca a API Key mantendo o credential_id"
    )
    rotate.add_argument("--credential-id", required=True, type=_credential_id)

    revoke = commands.add_parser("revoke", help="Revoga imediatamente uma API Key")
    revoke.add_argument("--credential-id", required=True, type=_credential_id)

    metadata = commands.add_parser(
        "metadata", help="Consulta metadados sem recuperar a API Key"
    )
    metadata.add_argument("--credential-id", required=True, type=_credential_id)

    webhook = commands.add_parser(
        "webhook", help="Configura destino HTTPS e material de assinatura"
    )
    webhook_commands = webhook.add_subparsers(dest="webhook_action", required=True)
    configure_webhook = webhook_commands.add_parser(
        "configure", help="Configura destino inicial e emite segredo uma vez"
    )
    configure_webhook.add_argument("--account-id", required=True, type=_account_id)
    configure_webhook.add_argument("--url", required=True)

    set_webhook_endpoint = webhook_commands.add_parser(
        "set-endpoint", help="Troca o destino sem rotacionar o segredo"
    )
    set_webhook_endpoint.add_argument("--account-id", required=True, type=_account_id)
    set_webhook_endpoint.add_argument("--url", required=True)

    rotate_webhook = webhook_commands.add_parser(
        "rotate", help="Emite material novo com 72 h de sobreposição"
    )
    rotate_webhook.add_argument("--account-id", required=True, type=_account_id)
    rotate_webhook.add_argument(
        "--immediate",
        action="store_true",
        help="Revoga material anterior imediatamente em caso de comprometimento",
    )

    disable_webhook = webhook_commands.add_parser(
        "disable", help="Desativa o destino e revoga seus materiais ativos"
    )
    disable_webhook.add_argument("--account-id", required=True, type=_account_id)

    args = parser.parse_args()
    engine = create_database_engine(database_url_from_env())
    try:
        upgrade_database(engine)
        if args.command == "webhook":
            cipher = WebhookSecretCipher.from_hex(
                os.getenv("WEBHOOK_SECRET_ENCRYPTION_KEY")
            )
            store = WebhookConfigStore(engine, cipher)
            if args.webhook_action == "configure":
                secret = store.configure(args.account_id, args.url)
                result = {"accountId": str(args.account_id), "webhookSecret": secret}
            elif args.webhook_action == "set-endpoint":
                store.set_endpoint(args.account_id, args.url)
                result = {"accountId": str(args.account_id), "endpointUpdated": True}
            elif args.webhook_action == "rotate":
                secret = store.rotate(args.account_id, immediate=args.immediate)
                result = {"accountId": str(args.account_id), "webhookSecret": secret}
            else:
                store.disable(args.account_id)
                result = {"accountId": str(args.account_id), "disabled": True}
        else:
            store = CredentialStore(engine)
            if args.command == "provision":
                credential, api_key = store.provision_account(args.account_name)
                result = {"credential": credential, "apiKey": api_key}
            elif args.command == "issue":
                credential, api_key = store.issue_for_account(args.account_id)
                result = {"credential": credential, "apiKey": api_key}
            elif args.command == "rotate":
                credential, api_key = store.rotate(args.credential_id)
                result = {"credential": credential, "apiKey": api_key}
            elif args.command == "revoke":
                result = {"credential": store.revoke(args.credential_id)}
            else:
                result = {"credential": store.metadata(args.credential_id)}
        print(json.dumps(result, ensure_ascii=False, indent=2))
        return 0
    except (CredentialNotFound, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 2
    finally:
        engine.dispose()


if __name__ == "__main__":
    raise SystemExit(main())
