"""Operator console and process-scoped session authentication."""

from __future__ import annotations

import hmac
import secrets

from fastapi import APIRouter, HTTPException, Request, Response, status
from fastapi.responses import HTMLResponse, JSONResponse
from pydantic import BaseModel, ConfigDict, Field


OPERATOR_SESSION_COOKIE = "operator_session"
OPERATOR_ROUTE_PREFIXES = (
    "/v1/accounts",
    "/v1/credentials",
    "/v1/queue",
    "/v1/jobs",
    "/v1/delivery-attempts",
)

router = APIRouter()


class OpenOperatorSessionRequest(BaseModel):
    model_config = ConfigDict(extra="forbid")

    secret: str = Field(min_length=1)


def is_operator_contract_path(path: str) -> bool:
    return any(
        path == prefix or path.startswith(f"{prefix}/")
        for prefix in OPERATOR_ROUTE_PREFIXES
    )


def _session_fingerprint(secret: str, session_id: str) -> bytes:
    return hmac.digest(secret.encode("utf-8"), session_id.encode("utf-8"), "sha256")


def is_operator_session_valid(request: Request) -> bool:
    secret = getattr(request.app.state, "operator_secret", None)
    session_id = request.cookies.get(OPERATOR_SESSION_COOKIE)
    sessions = getattr(request.app.state, "operator_sessions", {})
    if not secret or not session_id:
        return False

    stored_fingerprint = sessions.get(session_id)
    if stored_fingerprint is None:
        return False

    expected_fingerprint = _session_fingerprint(secret, session_id)
    if not hmac.compare_digest(stored_fingerprint, expected_fingerprint):
        # A secret rotation invalidates the identifier permanently, including
        # if the process is later configured with the previous value again.
        sessions.pop(session_id, None)
        return False
    return True


def unauthorized_operator_response(path: str) -> JSONResponse:
    return JSONResponse(
        status_code=status.HTTP_401_UNAUTHORIZED,
        media_type="application/problem+json",
        content={
            "type": "about:blank",
            "title": "Autenticação necessária",
            "status": status.HTTP_401_UNAUTHORIZED,
            "code": "UNAUTHORIZED",
            "detail": "Credencial inválida.",
            "instance": path,
        },
    )


@router.post("/v1/operator-sessions", status_code=status.HTTP_204_NO_CONTENT)
def open_operator_session(
    payload: OpenOperatorSessionRequest, request: Request
) -> Response:
    configured_secret = getattr(request.app.state, "operator_secret", None)
    if not configured_secret or not hmac.compare_digest(
        payload.secret.encode("utf-8"), configured_secret.encode("utf-8")
    ):
        raise HTTPException(status_code=status.HTTP_401_UNAUTHORIZED)

    session_id = secrets.token_urlsafe(32)
    request.app.state.operator_sessions[session_id] = _session_fingerprint(
        configured_secret, session_id
    )
    response = Response(status_code=status.HTTP_204_NO_CONTENT)
    response.set_cookie(
        OPERATOR_SESSION_COOKIE,
        session_id,
        httponly=True,
        path="/",
        samesite="lax",
    )
    return response


@router.delete("/v1/operator-sessions", status_code=status.HTTP_204_NO_CONTENT)
def close_operator_session(request: Request) -> Response:
    session_id = request.cookies.get(OPERATOR_SESSION_COOKIE)
    if session_id:
        request.app.state.operator_sessions.pop(session_id, None)
    response = Response(status_code=status.HTTP_204_NO_CONTENT)
    response.delete_cookie(
        OPERATOR_SESSION_COOKIE,
        path="/",
        httponly=True,
        samesite="lax",
    )
    return response


_LOGIN_PAGE = """<!doctype html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Console operacional</title>
</head>
<body>
  <main>
    <h1>Console operacional</h1>
    <form id="operator-login">
      <label for="operator-secret">Credencial operacional</label>
      <input id="operator-secret" name="secret" type="password" autocomplete="current-password" required>
      <button type="submit">Entrar</button>
    </form>
    <p id="login-error" role="alert" hidden>Não foi possível abrir a sessão.</p>
  </main>
  <script>
    const form = document.getElementById("operator-login");
    const error = document.getElementById("login-error");
    form.addEventListener("submit", async (event) => {
      event.preventDefault();
      error.hidden = true;
      const secretInput = form.elements.secret;
      const secret = secretInput.value;
      secretInput.value = "";
      try {
        const response = await fetch("/v1/operator-sessions", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ secret }),
        });
        if (!response.ok) {
          error.hidden = false;
          return;
        }
        window.location.reload();
      } catch {
        error.hidden = false;
      }
    });
  </script>
</body>
</html>"""

_CONSOLE_PAGE = """<!doctype html>
<html lang="pt-BR">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Console operacional</title>
</head>
<body>
  <main>
    <h1>Console operacional</h1>
    <button id="operator-logout" type="button">Encerrar sessão</button>
    <section aria-labelledby="accounts-heading">
      <h2 id="accounts-heading">Contas</h2>
      <form id="account-create">
        <label for="account-name">Nome da conta</label>
        <input id="account-name" name="name" maxlength="200" required>
        <button type="submit">Criar conta</button>
      </form>
      <p id="account-error" role="alert" hidden></p>
      <p id="account-status" role="status"></p>
      <ul id="account-list"></ul>
      <nav aria-label="Páginas de contas">
        <button id="accounts-previous" type="button" disabled>Anterior</button>
        <span id="accounts-pagination"></span>
        <button id="accounts-next" type="button" disabled>Próxima</button>
      </nav>
    </section>
    <section aria-labelledby="credentials-heading"><h2 id="credentials-heading">Credenciais</h2></section>
    <section aria-labelledby="destinations-heading"><h2 id="destinations-heading">Destinos de webhook</h2></section>
    <section aria-labelledby="queue-heading"><h2 id="queue-heading">Fila e jobs</h2></section>
    <section aria-labelledby="attempts-heading"><h2 id="attempts-heading">Tentativas de entrega</h2></section>
  </main>
  <script>
    document.getElementById("operator-logout").addEventListener("click", async () => {
      await fetch("/v1/operator-sessions", { method: "DELETE" });
      window.location.reload();
    });
    const accountForm = document.getElementById("account-create");
    const accountError = document.getElementById("account-error");
    const accountStatus = document.getElementById("account-status");
    const previousPage = document.getElementById("accounts-previous");
    const nextPage = document.getElementById("accounts-next");
    let accountPage = 1;

    async function loadAccounts(page = 1) {
      const response = await fetch(`/v1/accounts?_page=${page}&_size=10`);
      if (response.status === 401) {
        window.location.reload();
        return;
      }
      if (!response.ok) throw new Error("Não foi possível carregar as contas.");
      const result = await response.json();
      const list = document.getElementById("account-list");
      list.replaceChildren();
      for (const account of result.data) {
        const item = document.createElement("li");
        item.textContent = `${account.name} (${account.accountId})`;
        list.appendChild(item);
      }
      accountPage = result.pagination.page;
      document.getElementById("accounts-pagination").textContent =
        result.pagination.total === 0 ? "Nenhuma conta cadastrada." :
        `Página ${accountPage} de ${result.pagination.totalPages}`;
      previousPage.disabled = accountPage <= 1;
      nextPage.disabled = accountPage >= result.pagination.totalPages;
    }

    function showAccountError(message) {
      accountError.textContent = message;
      accountError.hidden = false;
    }

    accountForm.addEventListener("submit", async (event) => {
      event.preventDefault();
      accountError.hidden = true;
      accountStatus.textContent = "";
      const submit = accountForm.querySelector('button[type="submit"]');
      submit.disabled = true;
      try {
        const response = await fetch("/v1/accounts", {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ name: accountForm.elements.name.value }),
        });
        if (response.status === 401) {
          window.location.reload();
          return;
        }
        if (!response.ok) {
          const problem = await response.json();
          showAccountError(problem.code === "ACCOUNT_NAME_REQUIRED" ?
            "Informe o nome da conta." : "Não foi possível criar a conta.");
          return;
        }
        const account = await response.json();
        accountStatus.textContent = `Conta criada: ${account.name} (${account.accountId}).`;
        accountForm.reset();
        await loadAccounts();
      } catch {
        showAccountError("Não foi possível atualizar as contas. Consulte a lista antes de tentar novamente.");
      } finally {
        submit.disabled = false;
      }
    });
    previousPage.addEventListener("click", () => {
      loadAccounts(accountPage - 1).catch((error) => showAccountError(error.message));
    });
    nextPage.addEventListener("click", () => {
      loadAccounts(accountPage + 1).catch((error) => showAccountError(error.message));
    });
    loadAccounts().catch((error) => showAccountError(error.message));
  </script>
</body>
</html>"""


@router.get("/operator", response_class=HTMLResponse, include_in_schema=False)
def operator_console(request: Request) -> HTMLResponse:
    page = _CONSOLE_PAGE if is_operator_session_valid(request) else _LOGIN_PAGE
    return HTMLResponse(page, headers={"Cache-Control": "no-store"})
