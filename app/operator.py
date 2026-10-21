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
    <section aria-labelledby="credentials-heading">
      <h2 id="credentials-heading">Credenciais</h2>
      <p id="credential-account">Selecione uma conta na lista.</p>
      <button id="credential-issue" type="button" disabled>Emitir API Key</button>
      <p id="credential-error" role="alert" hidden></p>
      <div id="credential-secret" hidden>
        <p>Copie a API Key agora. Esta é a única exibição. Em caso de perda, será necessário rotacionar a chave.</p>
        <label for="credential-api-key">API Key</label>
        <input id="credential-api-key" type="text" readonly autocomplete="off">
        <button id="credential-copy" type="button">Copiar API Key</button>
        <button id="credential-dismiss" type="button">Fechar segredo</button>
        <p id="credential-copy-status" role="status"></p>
      </div>
      <ul id="credential-list"></ul>
      <nav aria-label="Páginas de credenciais">
        <button id="credentials-previous" type="button" disabled>Anterior</button>
        <span id="credentials-pagination"></span>
        <button id="credentials-next" type="button" disabled>Próxima</button>
      </nav>
      <pre id="credential-detail"></pre>
      <button id="credential-rotate" type="button" disabled>Rotacionar API Key</button>
      <button id="credential-revoke" type="button" disabled>Revogar API Key</button>
      <form id="credential-revoke-form" hidden>
        <label for="credential-revoke-reason">Motivo da revogação</label>
        <input id="credential-revoke-reason" name="reason" required autocomplete="off">
        <p>A revogação é imediata e impede novas chamadas com esta chave.</p>
        <button id="credential-revoke-confirm" type="submit">Confirmar revogação</button>
        <button id="credential-revoke-cancel" type="button">Cancelar</button>
      </form>
    </section>
    <section aria-labelledby="destinations-heading">
      <h2 id="destinations-heading">Destinos de webhook</h2>
      <p id="destination-account">Selecione uma conta na lista.</p>
      <button id="account-delivery-open" type="button" disabled>Ver tentativas desta conta</button>
      <form id="destination-form">
        <fieldset id="destination-fields" disabled>
          <legend>Endereço do webhook</legend>
          <label for="destination-url">URL do destino</label>
          <input id="destination-url" name="url" type="url" required autocomplete="off">
          <button type="submit">Salvar destino</button>
          <button id="destination-refresh" type="button">Consultar destino</button>
        </fieldset>
      </form>
      <p id="destination-error" role="alert" hidden></p>
      <p id="destination-status" role="status"></p>
      <pre id="destination-detail"></pre>
      <fieldset id="destination-lifecycle-fields" disabled>
        <legend>Material e desativação</legend>
        <button id="destination-rotate-planned" type="button">Rotacionar material com sobreposição de 72 horas</button>
        <button id="destination-rotate-immediate" type="button">Rotacionar material imediatamente</button>
        <button id="destination-deactivate" type="button">Desativar destino</button>
        <form id="destination-reason-form" hidden>
          <p id="destination-confirmation"></p>
          <label for="destination-reason">Motivo</label>
          <input id="destination-reason" name="reason" required autocomplete="off">
          <button type="submit">Confirmar</button>
          <button id="destination-cancel" type="button">Cancelar</button>
        </form>
      </fieldset>
      <div id="destination-secret" hidden>
        <p>Copie o material de verificação agora. Esta é a única exibição.</p>
        <label for="destination-material">Material de verificação</label>
        <input id="destination-material" type="text" readonly autocomplete="off">
        <button id="destination-copy" type="button">Copiar material</button>
        <button id="destination-dismiss" type="button">Fechar material</button>
        <p id="destination-copy-status" role="status"></p>
      </div>
    </section>
    <section aria-labelledby="queue-heading">
      <h2 id="queue-heading">Fila e jobs</h2>
      <p id="queue-summary" role="status">Carregando fila…</p>
      <button id="queue-refresh" type="button">Atualizar fila e jobs</button>
      <p id="queue-error" role="alert" hidden></p>
      <ul id="job-list"></ul>
      <nav aria-label="Páginas de jobs">
        <button id="jobs-previous" type="button" disabled>Anterior</button>
        <span id="jobs-pagination"></span>
        <button id="jobs-next" type="button" disabled>Próxima</button>
      </nav>
      <pre id="job-detail"></pre>
    </section>
    <section aria-labelledby="delivery-heading">
      <h2 id="delivery-heading">Tentativas de entrega</h2>
      <p id="delivery-context">Abra as tentativas de uma conta ou de um job.</p>
      <p>Última tentativa guardada de cada evento.</p>
      <label for="delivery-filter">Retentativa</label>
      <select id="delivery-filter">
        <option value="">Todas</option>
        <option value="true">Pendente de retentativa</option>
        <option value="false">Sem nova tentativa</option>
      </select>
      <button id="delivery-refresh" type="button" disabled>Atualizar tentativas</button>
      <p id="delivery-error" role="alert" hidden></p>
      <ul id="delivery-list"></ul>
      <nav aria-label="Páginas de tentativas">
        <button id="delivery-previous" type="button" disabled>Anterior</button>
        <span id="delivery-pagination"></span>
        <button id="delivery-next" type="button" disabled>Próxima</button>
      </nav>
    </section>
  </main>
  <script>
    document.getElementById("operator-logout").addEventListener("click", async () => {
      clearCredentialSecret();
      clearDestinationSecret();
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
        const open = document.createElement("button");
        open.type = "button";
        open.textContent = "Abrir conta e credenciais";
        open.addEventListener("click", () => {
          selectAccount(account.accountId).catch((error) => showCredentialError(error.message));
        });
        item.appendChild(open);
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
    const credentialIssue = document.getElementById("credential-issue");
    const credentialError = document.getElementById("credential-error");
    const credentialDetail = document.getElementById("credential-detail");
    const credentialPrevious = document.getElementById("credentials-previous");
    const credentialNext = document.getElementById("credentials-next");
    const credentialRotate = document.getElementById("credential-rotate");
    const credentialRevoke = document.getElementById("credential-revoke");
    const credentialRevokeForm = document.getElementById("credential-revoke-form");
    const credentialRevokeConfirm = document.getElementById("credential-revoke-confirm");
    let selectedAccountId = null;
    let selectedCredential = null;
    let credentialMutationPending = false;
    let credentialPage = 1;
    let credentialView = 0;

    function clearCredentialSecret() {
      credentialView += 1;
      document.getElementById("credential-api-key").value = "";
      document.getElementById("credential-secret").hidden = true;
      document.getElementById("credential-copy-status").textContent = "";
    }

    function showCredentialError(message) {
      credentialError.textContent = message;
      credentialError.hidden = false;
    }

    function updateCredentialActions() {
      const disabled = credentialMutationPending || selectedCredential?.status !== "active";
      credentialRotate.disabled = disabled;
      credentialRevoke.disabled = disabled;
      credentialRevokeConfirm.disabled = disabled;
      credentialIssue.disabled = credentialMutationPending || selectedAccountId === null;
    }

    function resetCredentialDetail() {
      selectedCredential = null;
      credentialDetail.textContent = "";
      credentialRevokeForm.hidden = true;
      credentialRevokeForm.reset();
      updateCredentialActions();
    }

    function showCredentialDetail(metadata) {
      selectedCredential = metadata;
      credentialDetail.textContent = JSON.stringify(metadata, null, 2);
      updateCredentialActions();
    }

    async function credentialRequest(path, options) {
      const response = await fetch(path, { cache: "no-store", ...options });
      if (response.status === 401) {
        clearCredentialSecret();
        window.location.reload();
      }
      if (!response.ok) {
        const problem = await response.json();
        if (problem.code === "REASON_REQUIRED") throw new Error("Informe o motivo antes de confirmar.");
        if (problem.code === "CREDENTIAL_REVOKED") throw new Error("Uma credencial revogada não é rotacionada.");
        throw new Error("Não foi possível concluir a operação de credenciais. Consulte a lista antes de tentar novamente.");
      }
      return response.json();
    }

    async function selectAccount(accountId) {
      clearCredentialSecret();
      resetDestination();
      resetDelivery();
      document.getElementById("account-delivery-open").disabled = true;
      const view = credentialView;
      selectedAccountId = null;
      credentialIssue.disabled = true;
      credentialPrevious.disabled = true;
      credentialNext.disabled = true;
      credentialError.hidden = true;
      resetCredentialDetail();
      document.getElementById("credential-account").textContent = "Carregando conta…";
      document.getElementById("credential-list").replaceChildren();
      document.getElementById("credentials-pagination").textContent = "";
      const account = await credentialRequest(`/v1/accounts/${accountId}`);
      if (view !== credentialView) return;
      selectedAccountId = account.accountId;
      document.getElementById("account-delivery-open").disabled = false;
      document.getElementById("credential-account").textContent = `${account.name} (${account.accountId})`;
      document.getElementById("destination-account").textContent = `${account.name} (${account.accountId})`;
      updateCredentialActions();
      await Promise.all([
        loadCredentials(),
        loadDestination().catch((error) => showDestinationError(error.message)),
      ]);
    }

    async function loadCredentials(page = 1) {
      const accountId = selectedAccountId;
      if (!accountId) return;
      const result = await credentialRequest(`/v1/accounts/${accountId}/credentials?_page=${page}&_size=10`);
      if (accountId !== selectedAccountId) return;
      const list = document.getElementById("credential-list");
      list.replaceChildren();
      for (const credential of result.data) {
        const item = document.createElement("li");
        const open = document.createElement("button");
        open.type = "button";
        open.textContent = `${credential.credentialId} (${credential.status})`;
        open.addEventListener("click", async () => {
          clearCredentialSecret();
          resetCredentialDetail();
          const view = credentialView;
          try {
            const detail = await credentialRequest(`/v1/credentials/${credential.credentialId}`);
            if (view === credentialView) showCredentialDetail(detail);
          } catch (error) {
            showCredentialError(error.message);
          }
        });
        item.appendChild(open);
        list.appendChild(item);
      }
      credentialPage = result.pagination.page;
      document.getElementById("credentials-pagination").textContent =
        result.pagination.total === 0 ? "Nenhuma credencial emitida." :
        `Página ${credentialPage} de ${result.pagination.totalPages}`;
      credentialPrevious.disabled = credentialPage <= 1;
      credentialNext.disabled = credentialPage >= result.pagination.totalPages;
    }

    credentialIssue.addEventListener("click", async () => {
      if (credentialMutationPending) return;
      clearCredentialSecret();
      const view = credentialView;
      const accountId = selectedAccountId;
      if (!accountId) return;
      credentialError.hidden = true;
      resetCredentialDetail();
      credentialIssue.disabled = true;
      try {
        const issued = await credentialRequest(`/v1/accounts/${accountId}/credentials`, { method: "POST" });
        if (view !== credentialView) return;
        document.getElementById("credential-api-key").value = issued.apiKey;
        document.getElementById("credential-secret").hidden = false;
        const { apiKey, ...metadata } = issued;
        showCredentialDetail(metadata);
        await loadCredentials();
      } catch (error) {
        showCredentialError(error.message);
      } finally {
        credentialIssue.disabled = selectedAccountId === null;
      }
    });
    async function mutateCredential(action, reason) {
      if (credentialMutationPending || selectedCredential?.status !== "active") return;
      const credentialId = selectedCredential.credentialId;
      clearCredentialSecret();
      const view = credentialView;
      credentialMutationPending = true;
      credentialError.hidden = true;
      updateCredentialActions();
      try {
        const result = await credentialRequest(`/v1/credentials/${credentialId}/${action}`, {
          method: "POST",
          ...(action === "revoke" ? {
            headers: { "Content-Type": "application/json" },
            body: JSON.stringify({ reason }),
          } : {}),
        });
        if (view !== credentialView) return;
        const { apiKey, ...metadata } = result;
        showCredentialDetail(metadata);
        credentialRevokeForm.hidden = true;
        credentialRevokeForm.reset();
        if (action === "rotate") {
          document.getElementById("credential-api-key").value = apiKey;
          document.getElementById("credential-secret").hidden = false;
        }
        await loadCredentials(credentialPage);
      } catch (error) {
        if (view === credentialView) showCredentialError(error.message);
      } finally {
        credentialMutationPending = false;
        updateCredentialActions();
      }
    }
    credentialRotate.addEventListener("click", () => mutateCredential("rotate"));
    credentialRevoke.addEventListener("click", () => {
      clearCredentialSecret();
      credentialError.hidden = true;
      credentialRevokeForm.hidden = false;
      document.getElementById("credential-revoke-reason").focus();
    });
    credentialRevokeForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const reason = credentialRevokeForm.elements.reason.value.trim();
      if (!reason) {
        showCredentialError("Informe o motivo antes de confirmar.");
        return;
      }
      mutateCredential("revoke", reason);
    });
    document.getElementById("credential-revoke-cancel").addEventListener("click", () => {
      credentialRevokeForm.hidden = true;
      credentialRevokeForm.reset();
    });
    document.getElementById("credential-copy").addEventListener("click", async () => {
      const view = credentialView;
      try {
        await navigator.clipboard.writeText(document.getElementById("credential-api-key").value);
        if (view === credentialView) document.getElementById("credential-copy-status").textContent = "API Key copiada.";
      } catch {
        if (view === credentialView) document.getElementById("credential-copy-status").textContent = "Selecione e copie a API Key manualmente.";
      }
    });
    document.getElementById("credential-dismiss").addEventListener("click", clearCredentialSecret);
    window.addEventListener("pagehide", clearCredentialSecret);
    credentialPrevious.addEventListener("click", () => {
      clearCredentialSecret();
      resetCredentialDetail();
      loadCredentials(credentialPage - 1).catch((error) => showCredentialError(error.message));
    });
    credentialNext.addEventListener("click", () => {
      clearCredentialSecret();
      resetCredentialDetail();
      loadCredentials(credentialPage + 1).catch((error) => showCredentialError(error.message));
    });
    const destinationForm = document.getElementById("destination-form");
    const destinationFields = document.getElementById("destination-fields");
    const destinationError = document.getElementById("destination-error");
    const destinationStatus = document.getElementById("destination-status");
    const destinationDetail = document.getElementById("destination-detail");
    const destinationLifecycleFields = document.getElementById("destination-lifecycle-fields");
    const destinationReasonForm = document.getElementById("destination-reason-form");
    let destinationActive = false;
    let destinationAction = null;
    let destinationView = 0;
    let destinationMutationPending = false;
    let destinationLoading = false;

    function clearDestinationSecret() {
      destinationView += 1;
      document.getElementById("destination-material").value = "";
      document.getElementById("destination-secret").hidden = true;
      document.getElementById("destination-copy-status").textContent = "";
    }

    function resetDestination() {
      clearDestinationSecret();
      cancelDestinationAction();
      destinationActive = false;
      destinationLoading = false;
      destinationLifecycleFields.disabled = true;
      destinationForm.reset();
      destinationFields.disabled = true;
      destinationError.hidden = true;
      destinationStatus.textContent = "";
      destinationDetail.textContent = "";
      document.getElementById("destination-account").textContent = "Carregando conta…";
    }

    function showDestinationError(message) {
      destinationError.textContent = message;
      destinationError.hidden = false;
    }

    function updateDestinationActions() {
      destinationFields.disabled = !selectedAccountId || destinationMutationPending || destinationLoading;
      destinationLifecycleFields.disabled = destinationFields.disabled || !destinationActive;
    }

    function showDestination(metadata) {
      destinationActive = metadata.active;
      destinationForm.elements.url.value = metadata.url || "";
      destinationDetail.textContent = JSON.stringify(metadata, null, 2);
      destinationStatus.textContent = metadata.active ? "Destino ativo." : "Destino inativo.";
    }

    async function destinationRequest(accountId, options, action = "") {
      const response = await fetch(`/v1/accounts/${accountId}/webhook-destination${action}`, {
        cache: "no-store", ...options,
      });
      if (response.status === 401) {
        clearDestinationSecret();
        clearCredentialSecret();
        window.location.reload();
      }
      if (!response.ok) {
        const problem = await response.json();
        if (problem.code === "DESTINATION_NOT_FOUND" && !options) return null;
        if (problem.code === "REASON_REQUIRED") throw new Error("Informe o motivo antes de confirmar.");
        if (problem.code === "DESTINATION_REJECTED") {
          throw new Error("Endereço recusado. O destino anterior permanece. Verifique o endereço e os destinos permitidos.");
        }
        throw new Error("Não foi possível concluir a operação. Consulte o destino antes de tentar novamente.");
      }
      return response.json();
    }

    async function loadDestination() {
      clearDestinationSecret();
      cancelDestinationAction();
      const view = destinationView;
      const accountId = selectedAccountId;
      if (!accountId) return;
      destinationError.hidden = true;
      destinationLoading = true;
      updateDestinationActions();
      try {
        const destination = await destinationRequest(accountId);
        if (view !== destinationView || accountId !== selectedAccountId) return;
        if (destination) showDestination(destination);
        else {
          destinationActive = false;
          destinationForm.reset();
          destinationDetail.textContent = "";
          destinationStatus.textContent = "Nenhum destino configurado. A API Key continua válida sem avisos de webhook.";
        }
      } finally {
        if (view === destinationView) {
          destinationLoading = false;
          updateDestinationActions();
        }
      }
    }

    destinationForm.addEventListener("submit", async (event) => {
      event.preventDefault();
      if (destinationMutationPending || destinationLoading || !selectedAccountId) return;
      clearDestinationSecret();
      cancelDestinationAction();
      const view = destinationView;
      const accountId = selectedAccountId;
      destinationError.hidden = true;
      destinationMutationPending = true;
      updateDestinationActions();
      try {
        const result = await destinationRequest(accountId, {
          method: "PUT",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ url: destinationForm.elements.url.value }),
        });
        if (view !== destinationView || accountId !== selectedAccountId) return;
        const { verificationMaterial, ...metadata } = result;
        showDestination(metadata);
        if (verificationMaterial) {
          document.getElementById("destination-material").value = verificationMaterial;
          document.getElementById("destination-secret").hidden = false;
        }
      } catch (error) {
        if (view === destinationView) showDestinationError(error.message);
      } finally {
        destinationMutationPending = false;
        updateDestinationActions();
      }
    });
    function cancelDestinationAction() {
      destinationAction = null;
      destinationReasonForm.reset();
      destinationReasonForm.hidden = true;
    }

    function requestDestinationReason(action) {
      if (destinationLifecycleFields.disabled) return;
      clearDestinationSecret();
      cancelDestinationAction();
      destinationAction = action;
      document.getElementById("destination-confirmation").textContent = action === "immediate"
        ? "O material anterior deixará de valer imediatamente. Informe o motivo antes de confirmar."
        : "Novas entregas e pendências serão interrompidas, e os materiais deixarão de valer. Informe o motivo antes de confirmar.";
      destinationReasonForm.hidden = false;
      destinationReasonForm.elements.reason.focus();
    }

    async function mutateDestination(action, reason) {
      if (destinationLifecycleFields.disabled || !selectedAccountId) return;
      clearDestinationSecret();
      cancelDestinationAction();
      const view = destinationView;
      const accountId = selectedAccountId;
      destinationError.hidden = true;
      destinationMutationPending = true;
      updateDestinationActions();
      try {
        const operation = action === "deactivate" ? "/deactivate" : "/rotate";
        const payload = action === "deactivate" ? { reason } : { mode: action, ...(reason ? { reason } : {}) };
        const result = await destinationRequest(accountId, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify(payload),
        }, operation);
        if (view !== destinationView || accountId !== selectedAccountId) return;
        const { verificationMaterial, ...metadata } = result;
        showDestination(metadata);
        if (verificationMaterial) {
          document.getElementById("destination-material").value = verificationMaterial;
          document.getElementById("destination-secret").hidden = false;
        }
      } catch (error) {
        if (view === destinationView) showDestinationError(error.message);
      } finally {
        destinationMutationPending = false;
        updateDestinationActions();
      }
    }

    document.getElementById("destination-rotate-planned").addEventListener("click", () => mutateDestination("planned"));
    document.getElementById("destination-rotate-immediate").addEventListener("click", () => requestDestinationReason("immediate"));
    document.getElementById("destination-deactivate").addEventListener("click", () => requestDestinationReason("deactivate"));
    document.getElementById("destination-cancel").addEventListener("click", cancelDestinationAction);
    destinationReasonForm.addEventListener("submit", (event) => {
      event.preventDefault();
      const reason = destinationReasonForm.elements.reason.value.trim();
      if (!reason) {
        showDestinationError("Informe o motivo antes de confirmar.");
        return;
      }
      if (destinationAction) mutateDestination(destinationAction, reason);
    });
    document.getElementById("destination-refresh").addEventListener("click", () => {
      loadDestination().catch((error) => showDestinationError(error.message));
    });
    document.getElementById("destination-copy").addEventListener("click", async () => {
      const view = destinationView;
      try {
        await navigator.clipboard.writeText(document.getElementById("destination-material").value);
        if (view === destinationView) document.getElementById("destination-copy-status").textContent = "Material copiado.";
      } catch {
        if (view === destinationView) document.getElementById("destination-copy-status").textContent = "Selecione e copie o material manualmente.";
      }
    });
    document.getElementById("destination-dismiss").addEventListener("click", clearDestinationSecret);
    window.addEventListener("pagehide", clearDestinationSecret);
    const queueError = document.getElementById("queue-error");
    const jobsPrevious = document.getElementById("jobs-previous");
    const jobsNext = document.getElementById("jobs-next");
    let jobsPage = 1;
    let queueSummary = null;
    let jobsView = 0;
    let jobDetailView = 0;

    function showQueueError(error) {
      queueError.textContent = error.message;
      queueError.hidden = false;
    }

    async function queueRequest(path) {
      const response = await fetch(path, { cache: "no-store" });
      if (response.status === 401) {
        clearCredentialSecret();
        clearDestinationSecret();
        window.location.reload();
      }
      if (response.status === 404) throw new Error("Job não encontrado ou fora da retenção.");
      if (!response.ok) throw new Error("Não foi possível consultar a fila e os jobs. Atualize para tentar novamente.");
      return response.json();
    }

    function renderQueueSummary() {
      if (!queueSummary) return;
      const oldest = queueSummary.oldestAcceptedAt;
      const age = oldest ? Math.max(0, Math.floor((Date.now() - Date.parse(oldest)) / 1000)) : null;
      document.getElementById("queue-summary").textContent =
        `Jobs na fila: ${queueSummary.size}. ` +
        (age === null ? "Sem jobs pendentes." : `Idade do mais antigo: ${age} segundos (aceito em ${oldest}).`);
    }

    async function openJob(jobId) {
      const view = ++jobDetailView;
      document.getElementById("job-detail").textContent = "";
      queueError.hidden = true;
      try {
        const job = await queueRequest(`/v1/jobs/${encodeURIComponent(jobId)}`);
        if (view === jobDetailView) document.getElementById("job-detail").textContent = JSON.stringify(job, null, 2);
      } catch (error) {
        if (view === jobDetailView) showQueueError(error);
      }
    }

    async function loadQueue(page = 1) {
      const view = ++jobsView;
      ++jobDetailView;
      queueError.hidden = true;
      document.getElementById("job-detail").textContent = "";
      jobsPrevious.disabled = jobsNext.disabled = true;
      try {
        const [summary, result] = await Promise.all([
          queueRequest("/v1/queue"),
          queueRequest(`/v1/jobs?_page=${page}&_size=10`),
        ]);
        if (view !== jobsView) return;
        queueSummary = summary;
        renderQueueSummary();
        const list = document.getElementById("job-list");
        list.replaceChildren();
        for (const job of result.data) {
          const item = document.createElement("li");
          item.textContent = `${job.accountName} (${job.accountId}) — ${job.jobId}: ${job.state}; aceito em ${job.acceptedAt}` +
            (job.terminalAt ? `; encerrado em ${job.terminalAt}` : "") +
            (job.clientReference !== undefined && job.clientReference !== null ? `; referência: ${job.clientReference}` : "");
          const open = document.createElement("button");
          open.type = "button";
          open.textContent = "Consultar job";
          open.addEventListener("click", () => openJob(job.jobId));
          item.appendChild(open);
          const attempts = document.createElement("button");
          attempts.type = "button";
          attempts.textContent = "Ver tentativas deste job";
          attempts.addEventListener("click", () => openDelivery({
            accountId: job.accountId, jobId: job.jobId,
            label: `${job.accountName} (${job.accountId}) — job ${job.jobId}`,
          }));
          item.appendChild(attempts);
          list.appendChild(item);
        }
        jobsPage = result.pagination.page;
        document.getElementById("jobs-pagination").textContent = result.pagination.total === 0
          ? "Nenhum job retido." : `Página ${jobsPage} de ${result.pagination.totalPages}`;
        jobsPrevious.disabled = jobsPage <= 1;
        jobsNext.disabled = jobsPage >= result.pagination.totalPages;
      } catch (error) {
        if (view === jobsView) showQueueError(error);
      }
    }

    const deliveryError = document.getElementById("delivery-error");
    const deliveryFilter = document.getElementById("delivery-filter");
    const deliveryPrevious = document.getElementById("delivery-previous");
    const deliveryNext = document.getElementById("delivery-next");
    let deliveryContext = null;
    let deliveryPage = 1;
    let deliveryView = 0;

    function resetDelivery() {
      ++deliveryView;
      deliveryContext = null;
      deliveryError.hidden = true;
      deliveryPrevious.disabled = deliveryNext.disabled = true;
      document.getElementById("delivery-refresh").disabled = true;
      document.getElementById("delivery-context").textContent = "Abra as tentativas de uma conta ou de um job.";
      document.getElementById("delivery-list").replaceChildren();
      document.getElementById("delivery-pagination").textContent = "";
    }

    function openDelivery(context) {
      resetDelivery();
      deliveryContext = context;
      deliveryFilter.value = "";
      document.getElementById("delivery-context").textContent = context.label;
      document.getElementById("delivery-refresh").disabled = false;
      document.getElementById("delivery-heading").scrollIntoView();
      loadDelivery();
    }

    async function loadDelivery(page = 1) {
      if (!deliveryContext) return;
      const view = ++deliveryView;
      const context = deliveryContext;
      const query = new URLSearchParams({ _page: page, _size: 10, accountId: context.accountId });
      if (context.jobId) query.set("jobId", context.jobId);
      if (deliveryFilter.value) query.set("willRetry", deliveryFilter.value);
      deliveryError.hidden = true;
      deliveryPrevious.disabled = deliveryNext.disabled = true;
      document.getElementById("delivery-list").replaceChildren();
      document.getElementById("delivery-pagination").textContent = "";
      try {
        const response = await fetch(`/v1/delivery-attempts?${query}`, { cache: "no-store" });
        if (response.status === 401) {
          clearCredentialSecret();
          clearDestinationSecret();
          window.location.reload();
        }
        if (!response.ok) throw new Error("Não foi possível consultar as tentativas. Atualize para tentar novamente.");
        const result = await response.json();
        if (view !== deliveryView) return;
        const list = document.getElementById("delivery-list");
        for (const attempt of result.data) {
          const item = document.createElement("li");
          const state = attempt.willRetry ? "Pendente de retentativa" : (attempt.failure ? "Falha encerrada" : "Sem nova tentativa");
          item.textContent = `Conta ${attempt.accountId}; job ${attempt.jobId}; evento ${attempt.eventId}; tentativa ${attempt.attemptId}; em ${attempt.occurredAt}: ${state}. ` +
            (attempt.failure ? `${attempt.failure}. ` : "") +
            (attempt.destinationActive ? "Destino ativo." : "Destino inativo.");
          list.appendChild(item);
        }
        deliveryPage = result.pagination.page;
        document.getElementById("delivery-pagination").textContent = result.pagination.total === 0
          ? "Nenhuma tentativa guardada." : `Página ${deliveryPage} de ${result.pagination.totalPages}`;
        deliveryPrevious.disabled = deliveryPage <= 1;
        deliveryNext.disabled = deliveryPage >= result.pagination.totalPages;
        if (context.accountId === selectedAccountId) {
          loadDestination().catch((error) => showDestinationError(error.message));
        }
      } catch (error) {
        if (view !== deliveryView) return;
        deliveryError.textContent = error.message;
        deliveryError.hidden = false;
      }
    }

    document.getElementById("account-delivery-open").addEventListener("click", () => {
      if (selectedAccountId) openDelivery({
        accountId: selectedAccountId,
        label: document.getElementById("destination-account").textContent,
      });
    });
    deliveryFilter.addEventListener("change", () => loadDelivery());
    document.getElementById("delivery-refresh").addEventListener("click", () => loadDelivery(deliveryPage));
    deliveryPrevious.addEventListener("click", () => loadDelivery(deliveryPage - 1));
    deliveryNext.addEventListener("click", () => loadDelivery(deliveryPage + 1));
    document.getElementById("queue-refresh").addEventListener("click", () => loadQueue());
    jobsPrevious.addEventListener("click", () => loadQueue(jobsPage - 1));
    jobsNext.addEventListener("click", () => loadQueue(jobsPage + 1));
    const queueAgeTimer = window.setInterval(renderQueueSummary, 1000);
    window.addEventListener("pagehide", () => window.clearInterval(queueAgeTimer));
    loadQueue();
    loadAccounts().catch((error) => showAccountError(error.message));
  </script>
</body>
</html>"""


@router.get("/operator", response_class=HTMLResponse, include_in_schema=False)
def operator_console(request: Request) -> HTMLResponse:
    page = _CONSOLE_PAGE if is_operator_session_valid(request) else _LOGIN_PAGE
    return HTMLResponse(page, headers={"Cache-Control": "no-store"})
