function trimSlash(value) {
  return String(value || "").replace(/\/+$/, "");
}

function leadSlash(value) {
  const text = String(value || "");
  return text.startsWith("/") ? text : `/${text}`;
}

function buildUrl(baseUrl, apiPath) {
  return `${trimSlash(baseUrl)}${leadSlash(apiPath)}`;
}

async function postJson(url, token, payload, timeoutSeconds) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), Math.max(1, timeoutSeconds) * 1000);

  try {
    const response = await fetch(url, {
      method: "POST",
      signal: controller.signal,
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(payload),
    });

    const text = await response.text();
    let data = null;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch {
        data = { raw: text };
      }
    }

    if (!response.ok) {
      return {
        ok: false,
        status: "failed",
        message: `official API request failed: HTTP ${response.status}`,
        response: data,
      };
    }

    return {
      ok: true,
      status: "ok",
      response: data,
    };
  } finally {
    clearTimeout(timeout);
  }
}

async function loginAuto(config) {
  const login = config.autoLogin;
  const execution = config.execution;
  const api = execution.officialApi || {};

  if (!api.baseUrl) {
    return {
      ok: false,
      status: "failed",
      message: "officialApi.baseUrl is required",
    };
  }

  if (!execution.botToken) {
    return {
      ok: false,
      status: "failed",
      message: "execution.botToken is required",
    };
  }

  if (!login.account) {
    return {
      ok: false,
      status: "failed",
      message: "autoLogin.account is required",
    };
  }

  const loginPayload = {
    account: login.account,
    password: login.password,
    serverId: login.serverId,
    mode: login.mode,
    roleId: login.roleId,
    roleIndex: login.roleIndex,
    enterGame: login.enterGame,
    clientName: config.target.clientName,
    requestedAt: new Date().toISOString(),
  };

  const loginResult = await postJson(
    buildUrl(api.baseUrl, api.loginPath),
    execution.botToken,
    loginPayload,
    api.timeoutSeconds || 15,
  );

  if (!loginResult.ok || !login.enterGame) {
    return {
      ...loginResult,
      status: loginResult.ok ? "loggedIn" : loginResult.status,
      message: loginResult.ok ? "official login API completed" : loginResult.message,
    };
  }

  const enterGamePayload = {
    account: login.account,
    serverId: login.serverId,
    roleId: login.roleId || loginResult.response?.roleId || "",
    roleIndex: login.roleIndex,
    loginResponse: loginResult.response,
    requestedAt: new Date().toISOString(),
  };

  const enterResult = await postJson(
    buildUrl(api.baseUrl, api.enterGamePath),
    execution.botToken,
    enterGamePayload,
    api.timeoutSeconds || 15,
  );

  return {
    ...enterResult,
    status: enterResult.ok ? "enteredGame" : enterResult.status,
    message: enterResult.ok ? "official enter-game API completed" : enterResult.message,
    loginResponse: loginResult.response,
  };
}

module.exports = {
  loginAuto,
};
