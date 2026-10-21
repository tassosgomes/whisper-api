"""V-01 operator session and console authentication tests."""

from __future__ import annotations

from unittest.mock import Mock

import pytest


def _open_session(client, secret: str | None = None):
    if secret is None:
        secret = client.app.state.operator_secret
    return client.post("/v1/operator-sessions", json={"secret": secret})


def _assert_unauthorized(response, path: str) -> None:
    assert response.status_code == 401
    assert response.headers["content-type"].split(";")[0] == "application/problem+json"
    assert response.json() == {
        "type": "about:blank",
        "title": "Autenticação necessária",
        "status": 401,
        "code": "UNAUTHORIZED",
        "detail": "Credencial inválida.",
        "instance": path,
    }


def test_v01_operator_session_screen_without_cookie_contains_only_login_form(client):
    response = client.get("/operator")

    assert response.status_code == 200
    assert response.headers["cache-control"] == "no-store"
    assert 'id="operator-login"' in response.text
    assert 'type="password"' in response.text
    assert "Contas" not in response.text
    assert "Credenciais" not in response.text
    assert "Destinos de webhook" not in response.text
    assert "Fila e jobs" not in response.text
    assert "Tentativas de entrega" not in response.text
    assert "listAccounts" not in response.text


@pytest.mark.parametrize(
    ("method", "path"),
    [
        ("GET", "/v1/accounts"),
        ("POST", "/v1/accounts"),
        ("GET", "/v1/accounts/00000000-0000-0000-0000-000000000001"),
        (
            "GET",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/credentials",
        ),
        (
            "POST",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/credentials",
        ),
        ("GET", "/v1/credentials/00000000-0000-0000-0000-000000000001"),
        (
            "POST",
            "/v1/credentials/00000000-0000-0000-0000-000000000001/rotate",
        ),
        (
            "POST",
            "/v1/credentials/00000000-0000-0000-0000-000000000001/revoke",
        ),
        (
            "GET",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/webhook-destination",
        ),
        (
            "PUT",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/webhook-destination",
        ),
        (
            "POST",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/webhook-destination/rotate",
        ),
        (
            "POST",
            "/v1/accounts/00000000-0000-0000-0000-000000000001/webhook-destination/deactivate",
        ),
        ("GET", "/v1/queue"),
        ("GET", "/v1/jobs"),
        ("GET", "/v1/jobs/job-id"),
        ("GET", "/v1/delivery-attempts"),
    ],
)
def test_v01_operator_session_all_contract_routes_require_session(client, method, path):
    response = client.request(method, path)

    _assert_unauthorized(response, path)


def test_v01_operator_session_correct_secret_opens_httponly_cookie_and_console(client):
    secret = client.app.state.operator_secret
    response = _open_session(client, secret)

    assert response.status_code == 204
    assert response.content == b""
    cookie_header = response.headers["set-cookie"]
    session_id = client.cookies.get("operator_session")
    assert session_id
    assert session_id != secret
    assert secret not in cookie_header
    assert "httponly" in cookie_header.casefold()
    assert "path=/" in cookie_header.casefold()
    assert "samesite=lax" in cookie_header.casefold()

    screen = client.get("/operator")
    assert screen.status_code == 200
    assert 'id="operator-login"' not in screen.text
    assert "Contas" in screen.text
    assert "Credenciais" in screen.text
    assert "Destinos de webhook" in screen.text
    assert "Fila e jobs" in screen.text
    assert "Tentativas de entrega" in screen.text
    # The account route is available once V-02 is implemented.
    assert client.get("/v1/accounts").status_code == 200


def test_v01_operator_session_wrong_or_unconfigured_secret_returns_401(
    client, monkeypatch
):
    configured_secret = client.app.state.operator_secret
    wrong = _open_session(client, "wrong-operator-secret")
    _assert_unauthorized(wrong, "/v1/operator-sessions")
    assert client.cookies.get("operator_session") is None

    monkeypatch.setattr(client.app.state, "operator_secret", None)
    unconfigured = _open_session(client, configured_secret)
    _assert_unauthorized(unconfigured, "/v1/operator-sessions")
    assert client.cookies.get("operator_session") is None


def test_v01_operator_session_rejects_customer_api_key_without_authenticating_it(
    client, monkeypatch
):
    _, api_key = client.app.state.credential_store.provision_account("Test account")
    authenticate = Mock(wraps=client.app.state.credential_store.authenticate)
    monkeypatch.setattr(client.app.state.credential_store, "authenticate", authenticate)

    response = _open_session(client, api_key)

    _assert_unauthorized(response, "/v1/operator-sessions")
    authenticate.assert_not_called()
    assert client.cookies.get("operator_session") is None


def test_v01_operator_session_cookie_does_not_authenticate_public_transcriptions(
    client,
):
    assert _open_session(client).status_code == 204

    query_response = client.get("/v1/transcriptions/not-a-job")
    create_response = client.post(
        "/v1/transcriptions",
        headers={"Idempotency-Key": "operator-cookie-must-not-authenticate"},
        json={"sourceUrl": client.app.state.test_source_url},
    )

    assert query_response.status_code == 401
    assert query_response.json()["code"] == "UNAUTHORIZED"
    assert create_response.status_code == 401
    assert create_response.json()["code"] == "UNAUTHORIZED"


def test_v01_operator_session_close_invalidates_identifier(client):
    assert _open_session(client).status_code == 204
    session_id = client.cookies.get("operator_session")
    assert session_id

    closed = client.delete("/v1/operator-sessions")

    assert closed.status_code == 204
    assert client.cookies.get("operator_session") is None
    assert session_id not in client.app.state.operator_sessions
    _assert_unauthorized(client.get("/v1/accounts"), "/v1/accounts")


def test_v01_operator_session_secret_rotation_invalidates_open_sessions(
    client, monkeypatch
):
    assert _open_session(client).status_code == 204

    monkeypatch.setattr(client.app.state, "operator_secret", "rotated-operator-secret")

    _assert_unauthorized(client.get("/v1/accounts"), "/v1/accounts")
    screen = client.get("/operator")
    assert 'id="operator-login"' in screen.text


def test_v01_operator_session_secret_is_not_logged(client, caplog):
    secret = client.app.state.operator_secret
    response = _open_session(client, secret)
    wrong_secret = "wrong-operator-secret-that-must-not-be-logged"
    wrong_response = _open_session(client, wrong_secret)

    assert response.status_code == 204
    _assert_unauthorized(wrong_response, "/v1/operator-sessions")
    assert secret not in caplog.text
    assert wrong_secret not in caplog.text
    assert secret not in response.text
