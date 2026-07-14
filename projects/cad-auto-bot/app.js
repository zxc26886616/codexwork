const form = document.getElementById("configForm");
const serverState = document.getElementById("serverState");
const serverUrl = document.getElementById("serverUrl");
const lastExecution = document.getElementById("lastExecution");
const lastCommand = document.getElementById("lastCommand");
const executionMessage = document.getElementById("executionMessage");
const logList = document.getElementById("logList");
const loginButton = document.getElementById("loginButton");
const sessionConnectButton = document.getElementById("sessionConnectButton");
const sessionDisconnectButton = document.getElementById("sessionDisconnectButton");
const caohuaUsername = document.getElementById("caohuaUsername");
const caohuaPassword = document.getElementById("caohuaPassword");
const caohuaUserId = document.getElementById("caohuaUserId");
const caohuaToken = document.getElementById("caohuaToken");
const caohuaSdkParam = document.getElementById("caohuaSdkParam");
const caohuaState = document.getElementById("caohuaState");
const automationStartButton = document.getElementById("automationStartButton");
const automationRunButton = document.getElementById("automationRunButton");
const automationStopButton = document.getElementById("automationStopButton");
const automationState = document.getElementById("automationState");
const automationResultList = document.getElementById("automationResultList");
const serverDirectoryEnvironment = document.getElementById("serverDirectoryEnvironment");
const serverDirectorySelect = document.getElementById("serverDirectorySelect");
const loadServerDirectoryButton = document.getElementById("loadServerDirectoryButton");
const loadAccountServersButton = document.getElementById("loadAccountServersButton");
const serverDirectoryState = document.getElementById("serverDirectoryState");

let loaded = false;
let serverDirectory = [];
const serverStatusNames = ["流畅", "拥挤", "爆满", "维护中", "未开放", "内网"];

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

function fillAndroidChannelOptions(profiles, error) {
  const select = form.elements.androidChannelConfigId;
  select.innerHTML = "";
  const automatic = document.createElement("option");
  automatic.value = "0";
  automatic.textContent = error ? `读取 Channel.json 失败：${error}` : "由登录代理返回完整 sdkParam";
  select.appendChild(automatic);
  for (const profile of profiles || []) {
    const option = document.createElement("option");
    option.value = String(profile.id);
    const applyTail = profile.channelApplyId.slice(-8);
    option.textContent = `配置 ${profile.id}｜App ${profile.appId}｜渠道 ${profile.channelId}｜申请尾号 ${applyTail}`;
    select.appendChild(option);
  }
}

function fillForm(config) {
  setField("unityProjectPath", config.target.unityProjectPath);
  setField("clientName", config.target.clientName);
  setField("adapter", config.execution.adapter);
  setField("environment", config.execution.environment);
  setField("productionGuard", config.execution.productionGuard);
  setField("rateLimitSeconds", config.execution.rateLimitSeconds);
  setField("dryRun", config.execution.dryRun);
  setField("officialBaseUrl", config.execution.officialApi?.baseUrl);
  setField("officialLoginPath", config.execution.officialApi?.loginPath);
  setField("officialTimeoutSeconds", config.execution.officialApi?.timeoutSeconds);
  setField("gatewayEnabled", config.gateway?.enabled);
  setField("gatewayServerUrl", config.gateway?.serverUrl);
  setField("gatewayServerIp", config.gateway?.serverIp);
  setField("gatewayPort", config.gateway?.port);
  setField("gatewayServerNode", config.gateway?.serverNode);
  setField("gatewayTimeoutSeconds", config.gateway?.timeoutSeconds);
  setField("gatewayAutoReconnect", config.gateway?.autoReconnect);
  setField("gatewayReconnectAttempts", config.gateway?.reconnectAttempts);
  setField("gatewayReconnectIntervalSeconds", config.gateway?.reconnectIntervalSeconds);
  setField("codeVersion", config.gameplay?.codeVersion);
  setField("clientVersion", config.gameplay?.clientVersion);
  setField("gameLanguage", config.gameplay?.language);
  setField("deviceModel", config.gameplay?.deviceModel);
  setField("belongId", config.gameplay?.belongId);
  setField("androidChannelConfigId", config.androidChannel?.configId);
  setField("enabled", config.autoLogin.enabled);
  setField("mode", config.autoLogin.mode);
  setField("serverId", config.autoLogin.serverId);
  setField("roleId", config.autoLogin.roleId);
  setField("roleIndex", config.autoLogin.roleIndex);
  setField("enterGame", config.autoLogin.enterGame);
  setField("delaySeconds", config.autoLogin.delaySeconds);
  setField("retryTimes", config.autoLogin.retryTimes);
  setField("retryIntervalSeconds", config.autoLogin.retryIntervalSeconds);
  setField("automationEnabled", config.automation?.enabled);
  setField("automationIntervalMinutes", Math.max(1, Number(config.automation?.intervalSeconds || 3600) / 60));
  setField("claimCharacterMail", config.automation?.tasks?.claimCharacterMail);
  setField("claimAccountMail", config.automation?.tasks?.claimAccountMail);
  setField("dailySign", config.automation?.tasks?.dailySign);
  setField("claimTimedEnergy", config.automation?.tasks?.claimTimedEnergy);
  setField("claimDailyTasks", config.automation?.tasks?.claimDailyTasks);
  setField("claimDailyBoxes", config.automation?.tasks?.claimDailyBoxes);
  setField("wipeBattle", config.automation?.tasks?.wipeBattle);
  setField("wipeBattleId", config.automation?.wipeBattle?.battleId);
  setField("wipeBattleTimes", config.automation?.wipeBattle?.times);
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
      rateLimitSeconds: getField("rateLimitSeconds"),
      dryRun: getField("dryRun"),
      officialApi: {
        baseUrl: getField("officialBaseUrl"),
        loginPath: getField("officialLoginPath"),
        timeoutSeconds: getField("officialTimeoutSeconds"),
      },
    },
    gateway: {
      enabled: getField("gatewayEnabled"),
      serverUrl: getField("gatewayServerUrl"),
      serverIp: getField("gatewayServerIp"),
      port: getField("gatewayPort"),
      serverNode: getField("gatewayServerNode"),
      timeoutSeconds: getField("gatewayTimeoutSeconds"),
      autoReconnect: getField("gatewayAutoReconnect"),
      reconnectAttempts: getField("gatewayReconnectAttempts"),
      reconnectIntervalSeconds: getField("gatewayReconnectIntervalSeconds"),
    },
    gameplay: {
      codeVersion: getField("codeVersion"),
      clientVersion: getField("clientVersion"),
      language: getField("gameLanguage"),
      deviceModel: getField("deviceModel"),
      belongId: getField("belongId"),
    },
    androidChannel: {
      configId: getField("androidChannelConfigId"),
    },
    autoLogin: {
      enabled: getField("enabled"),
      mode: getField("mode"),
      serverId: getField("serverId"),
      roleId: getField("roleId"),
      roleIndex: getField("roleIndex"),
      enterGame: getField("enterGame"),
      delaySeconds: getField("delaySeconds"),
      retryTimes: getField("retryTimes"),
      retryIntervalSeconds: getField("retryIntervalSeconds"),
    },
    automation: {
      enabled: getField("automationEnabled"),
      intervalSeconds: Math.max(1, getField("automationIntervalMinutes")) * 60,
      tasks: {
        claimCharacterMail: getField("claimCharacterMail"),
        claimAccountMail: getField("claimAccountMail"),
        dailySign: getField("dailySign"),
        claimTimedEnergy: getField("claimTimedEnergy"),
        claimDailyTasks: getField("claimDailyTasks"),
        claimDailyBoxes: getField("claimDailyBoxes"),
        wipeBattle: getField("wipeBattle"),
      },
      wipeBattle: {
        battleId: getField("wipeBattleId"),
        times: getField("wipeBattleTimes"),
      },
    },
  };
}

async function requestJson(url, options = {}) {
  const response = await fetch(url, {
    headers: { "Content-Type": "application/json" },
    ...options,
  });
  const data = await response.json();
  if (!response.ok) {
    throw new Error(data.error || `HTTP ${response.status}`);
  }
  return data;
}

async function loadServerDirectory() {
  serverDirectoryState.textContent = "正在读取官方区服目录…";
  try {
    const data = await requestJson(
      `/api/server-directory?environment=${encodeURIComponent(serverDirectoryEnvironment.value)}`,
    );
    renderServerDirectory(data.servers || []);
    serverDirectoryState.textContent = `已加载 ${serverDirectory.length} 个区服，请明确选择；维护/未开放区服不可选`;
    serverDirectoryState.className = "notice success";
  } catch (error) {
    serverDirectoryState.textContent = `区服目录加载失败：${error.message}；仍可手工填写`;
    serverDirectoryState.className = "notice warning";
  }
}

function renderServerDirectory(servers) {
  serverDirectory = servers;
  serverDirectorySelect.innerHTML = "";
    const manual = document.createElement("option");
    manual.value = "";
    manual.textContent = "手工填写下方参数";
    serverDirectorySelect.appendChild(manual);
    for (const item of serverDirectory) {
      const option = document.createElement("option");
      option.value = item.id;
      option.disabled = !item.selectable;
      option.textContent = `${item.id}服｜${item.showName || item.serverNode}｜${
        serverStatusNames[item.status] || `状态 ${item.status}`
      }${item.areaName ? `｜${item.areaName}` : ""}`;
      serverDirectorySelect.appendChild(option);
    }
}

async function loadAccountServers() {
  serverDirectoryState.textContent = "正在查询当前草花账号的角色区服…";
  try {
    const data = await requestJson(
      `/api/account-servers?environment=${encodeURIComponent(serverDirectoryEnvironment.value)}`,
    );
    renderServerDirectory(data.servers || []);
    const unmatched = data.unmatchedGameNodes?.length
      ? `，另有 ${data.unmatchedGameNodes.length} 个节点不在当前目录`
      : "";
    serverDirectoryState.textContent = `账号共有 ${serverDirectory.length} 个可匹配角色区服${unmatched}，请明确选择`;
    serverDirectoryState.className = "notice success";
  } catch (error) {
    serverDirectoryState.textContent = `角色区服查询失败：${error.message}`;
    serverDirectoryState.className = "notice warning";
  }
}

serverDirectorySelect.addEventListener("change", () => {
  if (!serverDirectorySelect.value) return;
  const item = serverDirectory.find((entry) => entry.id === serverDirectorySelect.value);
  if (!item || !item.selectable) return;
  setField("gatewayServerUrl", item.serverUrl);
  setField("gatewayServerIp", item.serverIp);
  setField("gatewayPort", item.port);
  setField("gatewayServerNode", item.serverNode);
  setField("serverId", item.id);
  serverDirectoryState.textContent = `已选择 ${item.id}服 ${item.showName || item.serverNode}，保存参数后生效`;
  serverDirectoryState.className = "notice success";
});

loadServerDirectoryButton.addEventListener("click", loadServerDirectory);
loadAccountServersButton.addEventListener("click", loadAccountServers);

function renderStatus(data) {
  const { config, runtime } = data;
  serverState.textContent = "已连接";
  serverState.className = "server-pill online";
  const activeServer = runtime.server || config.server;
  serverUrl.textContent = `http://${activeServer.host}:${activeServer.port}`;
  lastExecution.textContent = runtime.lastExecution
    ? `${runtime.lastExecution.commandId} / ${runtime.lastExecution.status}`
    : "无";
  lastCommand.textContent = runtime.lastCommand
    ? `${runtime.lastCommand.id} / ${runtime.lastCommand.status}`
    : "无";
  executionMessage.textContent = runtime.lastExecution?.message || "无";
  const automation = runtime.automation || {};
  automationState.textContent = automation.running
    ? `${automation.inProgress ? "执行中" : "运行中"}，下次 ${formatTime(automation.nextRunAt)}`
    : automation.inProgress
      ? "正在执行一次性任务"
      : "未启动";
  automationResultList.innerHTML = "";
  for (const item of automation.lastRun?.results || []) {
    const li = document.createElement("li");
    const parts = [];
    if (item.detail?.claimedMailCount != null) parts.push(`邮件 ${item.detail.claimedMailCount}`);
    if (item.detail?.claimedTaskCount != null) parts.push(`任务 ${item.detail.claimedTaskCount}`);
    if (item.detail?.claimedBoxIndexes) parts.push(`宝箱 ${item.detail.claimedBoxIndexes.join("/")}`);
    if (item.detail?.battleId) parts.push(`副本 ${item.detail.battleId} × ${item.detail.times}`);
    if (item.detail?.rewardEntries != null || item.detail?.itemEntries != null) {
      parts.push(`奖励项 ${(item.detail.rewardEntries || 0) + (item.detail.itemEntries || 0)}`);
    }
    if (item.detail?.activePoint != null) parts.push(`活跃度 ${item.detail.activePoint}`);
    if (item.detail?.reason) parts.push(item.detail.reason);
    const detail = parts.length > 0 ? `，${parts.join("，")}` : "";
    li.textContent = `${item.label}：${item.status}${detail}${item.message ? `，${item.message}` : ""}`;
    automationResultList.appendChild(li);
  }
  const caohua = runtime.caohua || {};
  if (caohua.activeSession) {
    const activeSession = caohua.activeSession;
    if (activeSession.reconnect?.active) {
      caohuaState.textContent = `角色网关重连中：第 ${activeSession.reconnect.attempt}/${
        activeSession.reconnect.maxAttempts
      } 次，下次 ${formatTime(activeSession.reconnect.nextAttemptAt)}`;
    } else if (activeSession.roleOnline) {
      caohuaState.textContent = `角色持续在线：${activeSession.userId}，连接于 ${formatTime(
        activeSession.connectedAt,
      )}，最近心跳 ${formatTime(activeSession.health?.lastHeartbeatAt)}`;
    } else {
      caohuaState.textContent = `渠道凭证已就绪：${activeSession.userId}，来源 ${
        activeSession.source
      }，有效至 ${formatTime(activeSession.expiresAt)}`;
    }
    caohuaState.className = "notice success";
  } else {
    caohuaState.textContent = "等待草花账号密码，或 SDK 已签发的 userId/token";
    caohuaState.className = "notice";
  }

  logList.innerHTML = "";
  for (const item of runtime.logs || []) {
    const li = document.createElement("li");
    li.textContent = `${formatTime(item.time)} [${item.type}] ${item.message}`;
    logList.appendChild(li);
  }

  if (!loaded) {
    fillAndroidChannelOptions(data.androidChannels, data.androidChannelError);
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
  const credentials = {
    username: caohuaUsername.value.trim(),
    password: caohuaPassword.value,
    userId: caohuaUserId.value.trim(),
    channelToken: caohuaToken.value.trim(),
    sdkParam: caohuaSdkParam.value.trim(),
  };
  // 密码、渠道 token 与 SDK 参数只用于本次请求，读取后立即清空页面字段。
  caohuaPassword.value = "";
  caohuaToken.value = "";
  caohuaSdkParam.value = "";
  try {
    await requestJson("/api/config", {
      method: "PUT",
      body: JSON.stringify(collectConfig()),
    });
    await requestJson("/api/login/request", {
      method: "POST",
      body: JSON.stringify(credentials),
    });
    loaded = false;
    await refresh();
  } catch (error) {
    caohuaState.textContent = `草花登录失败：${error.message}`;
    caohuaState.className = "notice warning";
  }
});

sessionConnectButton.addEventListener("click", async () => {
  try {
    // 区服必须由用户明确选择；连接前仅保存当前表单，不自动改写区服参数。
    await saveCurrentConfig();
    await requestJson("/api/session/connect", { method: "POST", body: "{}" });
    loaded = false;
    await refresh();
  } catch (error) {
    caohuaState.textContent = `连接所选区服失败：${error.message}`;
    caohuaState.className = "notice warning";
  }
});

sessionDisconnectButton.addEventListener("click", async () => {
  try {
    await requestJson("/api/session/disconnect", { method: "POST", body: "{}" });
    loaded = false;
    await refresh();
  } catch (error) {
    caohuaState.textContent = `断开连接失败：${error.message}`;
    caohuaState.className = "notice warning";
  }
});

async function saveCurrentConfig() {
  await requestJson("/api/config", {
    method: "PUT",
    body: JSON.stringify(collectConfig()),
  });
}

async function runAutomationAction(path, saveFirst = false) {
  try {
    if (saveFirst) await saveCurrentConfig();
    await requestJson(path, { method: "POST", body: "{}" });
    loaded = false;
    await refresh();
  } catch (error) {
    automationState.textContent = `操作失败：${error.message}`;
  }
}

automationStartButton.addEventListener("click", () =>
  runAutomationAction("/api/automation/start", true),
);
automationRunButton.addEventListener("click", () =>
  runAutomationAction("/api/automation/run", true),
);
automationStopButton.addEventListener("click", () =>
  runAutomationAction("/api/automation/stop"),
);

refresh();
setInterval(refresh, 2000);
