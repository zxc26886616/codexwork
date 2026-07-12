const form = document.getElementById("configForm");
const serverState = document.getElementById("serverState");
const serverUrl = document.getElementById("serverUrl");
const lastExecution = document.getElementById("lastExecution");
const lastCommand = document.getElementById("lastCommand");
const executionMessage = document.getElementById("executionMessage");
const logList = document.getElementById("logList");
const loginButton = document.getElementById("loginButton");

let loaded = false;

function setField(name, value) {
  const field = form.elements[name];
  if (!field) return;
  if (field.type === "checkbox") {
    field.checked = Boolean(value);
  } else {
    field.value = value ?? "";
  }
}

function getField(name) {
  const field = form.elements[name];
  if (!field) return "";
  if (field.type === "checkbox") return field.checked;
  if (field.type === "number") return Number(field.value || 0);
  return field.value.trim();
}

function formatTime(value) {
  if (!value) return "未连接";
  return new Date(value).toLocaleString();
}

function fillForm(config) {
  setField("unityProjectPath", config.target.unityProjectPath);
  setField("clientName", config.target.clientName);
  setField("adapter", config.execution.adapter);
  setField("environment", config.execution.environment);
  setField("productionGuard", config.execution.productionGuard);
  setField("botToken", config.execution.botToken);
  setField("rateLimitSeconds", config.execution.rateLimitSeconds);
  setField("dryRun", config.execution.dryRun);
  setField("officialBaseUrl", config.execution.officialApi?.baseUrl);
  setField("officialLoginPath", config.execution.officialApi?.loginPath);
  setField("officialEnterGamePath", config.execution.officialApi?.enterGamePath);
  setField("officialTimeoutSeconds", config.execution.officialApi?.timeoutSeconds);
  setField("enabled", config.autoLogin.enabled);
  setField("mode", config.autoLogin.mode);
  setField("account", config.autoLogin.account);
  setField("password", config.autoLogin.password);
  setField("serverId", config.autoLogin.serverId);
  setField("roleId", config.autoLogin.roleId);
  setField("roleIndex", config.autoLogin.roleIndex);
  setField("enterGame", config.autoLogin.enterGame);
  setField("delaySeconds", config.autoLogin.delaySeconds);
  setField("retryTimes", config.autoLogin.retryTimes);
  setField("retryIntervalSeconds", config.autoLogin.retryIntervalSeconds);
}

function collectConfig() {
  return {
    target: {
      unityProjectPath: getField("unityProjectPath"),
      clientName: getField("clientName"),
    },
    execution: {
      adapter: getField("adapter"),
      environment: getField("environment"),
      productionGuard: getField("productionGuard"),
      botToken: getField("botToken"),
      rateLimitSeconds: getField("rateLimitSeconds"),
      dryRun: getField("dryRun"),
      officialApi: {
        baseUrl: getField("officialBaseUrl"),
        loginPath: getField("officialLoginPath"),
        enterGamePath: getField("officialEnterGamePath"),
        timeoutSeconds: getField("officialTimeoutSeconds"),
      },
    },
    autoLogin: {
      enabled: getField("enabled"),
      mode: getField("mode"),
      account: getField("account"),
      password: getField("password"),
      serverId: getField("serverId"),
      roleId: getField("roleId"),
      roleIndex: getField("roleIndex"),
      enterGame: getField("enterGame"),
      delaySeconds: getField("delaySeconds"),
      retryTimes: getField("retryTimes"),
      retryIntervalSeconds: getField("retryIntervalSeconds"),
    },
  };
}

async function requestJson(url, options = {}) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  if (!response.ok) {
    throw new Error(`HTTP ${response.status}`);
  }
  return response.json();
}

function renderStatus(data) {
  const { config, runtime } = data;
  serverState.textContent = "已连接";
  serverState.className = "server-pill online";
  serverUrl.textContent = `http://${config.server.host}:${config.server.port}`;
  lastExecution.textContent = runtime.lastExecution
    ? `${runtime.lastExecution.commandId} / ${runtime.lastExecution.status}`
    : "无";
  lastCommand.textContent = runtime.lastCommand
    ? `${runtime.lastCommand.id} / ${runtime.lastCommand.status}`
    : "无";
  executionMessage.textContent = runtime.lastExecution?.message || "无";

  logList.innerHTML = "";
  for (const item of runtime.logs || []) {
    const li = document.createElement("li");
    li.textContent = `${formatTime(item.time)} [${item.type}] ${item.message}`;
    logList.appendChild(li);
  }

  if (!loaded) {
    fillForm(config);
    loaded = true;
  }
}

async function refresh() {
  try {
    const data = await requestJson("/api/status");
    renderStatus(data);
  } catch (error) {
    serverState.textContent = "离线";
    serverState.className = "server-pill offline";
  }
}

form.addEventListener("submit", async (event) => {
  event.preventDefault();
  await requestJson("/api/config", {
    method: "PUT",
    body: JSON.stringify(collectConfig()),
  });
  loaded = false;
  await refresh();
});

loginButton.addEventListener("click", async () => {
  await requestJson("/api/config", {
    method: "PUT",
    body: JSON.stringify(collectConfig()),
  });
  await requestJson("/api/login/request", { method: "POST" });
  loaded = false;
  await refresh();
});

refresh();
setInterval(refresh, 2000);
