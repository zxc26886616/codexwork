const http = require("http");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const { URL } = require("url");
const {
  authenticateLoginGateway,
  buildLoginWebSocketUrl,
} = require("./protocol/cadLoginGatewayClient");
const { connectGameGateway } = require("./protocol/cadGameGatewayClient");
const { CadGameProtocolSession } = require("./protocol/cadGameProtocolSession");
const { loginSelectedRole } = require("./protocol/cadRoleLoginFlow");
const { CadOnlineKeeper } = require("./protocol/cadOnlineKeeper");
const { SprotoBridgeClient } = require("./protocol/sprotoBridgeClient");
const { CadTaskScheduler } = require("./automation/cadTaskScheduler");
const { CadGameStateStore } = require("./automation/cadGameStateStore");
const {
  promoteToPersistentSession,
  shouldExpireCredentialSession,
  stopGatewayResources,
} = require("./automation/cadSessionLifecycle");
const { CadSecretVault } = require("./automation/cadSecretVault");
const { CadReconnectLoop } = require("./automation/cadReconnectLoop");
const {
  connectExistingSession,
} = require("./automation/cadExistingSessionConnector");
const { loadAndroidChannelProfiles } = require("./protocol/cadAndroidSdkParam");
const { fetchOfficialServerDirectory } = require("./protocol/cadServerDirectory");
const {
  fetchAndroidRoleServers,
  matchRoleServers,
} = require("./protocol/cadAndroidRoleServers");

const rootDir = __dirname;
const dataDir = path.join(rootDir, "data");
const configPath = path.join(dataDir, "config.json");

const defaultConfig = {
  target: {
    unityProjectPath:
      "D:\\code\\cad-client-mas\\cadclient3.0_unity2021\\cadclient3.0_unity2021",
    clientName: "CAD Client 3.0",
  },
  server: {
    host: "127.0.0.1",
    port: 17830,
  },
  execution: {
    adapter: "mock",
    environment: "production",
    productionGuard: true,
    rateLimitSeconds: 10,
    dryRun: true,
    officialApi: {
      baseUrl: "",
      loginPath: "/bot/login",
      timeoutSeconds: 15,
    },
  },
  gateway: {
    enabled: false,
    serverUrl: "",
    serverIp: "",
    port: 10001,
    serverNode: "game1",
    timeoutSeconds: 15,
    autoReconnect: true,
    reconnectAttempts: 5,
    reconnectIntervalSeconds: 5,
  },
  gameplay: {
    codeVersion: "1.0.11",
    clientVersion: "v1.0.24",
    language: 0,
    deviceModel: "Android",
    belongId: "1002984",
  },
  androidChannel: {
    configId: 0,
  },
  autoLogin: {
    enabled: true,
    mode: "lastRole",
    serverId: "",
    roleId: "",
    roleIndex: 0,
    enterGame: true,
    delaySeconds: 2,
    retryTimes: 3,
    retryIntervalSeconds: 5,
  },
  automation: {
    enabled: false,
    intervalSeconds: 3600,
    tasks: {
      claimCharacterMail: true,
      claimAccountMail: true,
      dailySign: false,
      claimTimedEnergy: false,
      claimDailyTasks: false,
      claimDailyBoxes: false,
      wipeBattle: false,
    },
    wipeBattle: {
      battleId: "",
      times: 1,
    },
  },
};

const runtime = {
  startedAt: new Date().toISOString(),
  server: {
    host: "",
    port: 0,
  },
  lastCommand: null,
  lastExecution: null,
  commandSeq: 0,
  logs: [],
  caohua: {
    activeSession: null,
  },
  automation: {
    sessionId: null,
    running: false,
    inProgress: false,
    nextRunAt: null,
    lastRun: null,
    history: [],
    enabledTasks: [],
  },
};

// 草花 token 只能留在服务端内存中，不能写入配置、日志或返回给 Web 页面。
const caohuaSessions = new Map();
const CAOHUA_SESSION_TTL_MS = 30 * 60 * 1000;
const secretVault = new CadSecretVault();

function clone(value) {
  return JSON.parse(JSON.stringify(value));
}

function ensureConfig() {
  fs.mkdirSync(dataDir, { recursive: true });
  if (!fs.existsSync(configPath)) {
    fs.writeFileSync(configPath, JSON.stringify(defaultConfig, null, 2), "utf8");
  }
}

function readConfig() {
  ensureConfig();
  const raw = fs.readFileSync(configPath, "utf8");
  const saved = JSON.parse(raw);
  const savedExecution = saved.execution || {};
  const config = {
    ...clone(defaultConfig),
    ...saved,
    target: { ...defaultConfig.target, ...(saved.target || {}) },
    server: { ...defaultConfig.server, ...(saved.server || {}) },
    execution: {
      ...defaultConfig.execution,
      ...savedExecution,
      officialApi: {
        ...defaultConfig.execution.officialApi,
        ...(savedExecution.officialApi || {}),
      },
    },
    gateway: { ...defaultConfig.gateway, ...(saved.gateway || {}) },
    gameplay: { ...defaultConfig.gameplay, ...(saved.gameplay || {}) },
    androidChannel: {
      ...defaultConfig.androidChannel,
      ...(saved.androidChannel || {}),
    },
    autoLogin: { ...defaultConfig.autoLogin, ...(saved.autoLogin || {}) },
    automation: {
      ...defaultConfig.automation,
      ...(saved.automation || {}),
      tasks: {
        ...defaultConfig.automation.tasks,
        ...(saved.automation?.tasks || {}),
      },
      wipeBattle: {
        ...defaultConfig.automation.wipeBattle,
        ...(saved.automation?.wipeBattle || {}),
      },
    },
  };
  // 兼容旧配置文件，但永远不把历史明文密钥重新暴露给 API。
  delete config.execution.botToken;
  delete config.autoLogin.password;
  return config;
}

function writeConfig(config) {
  fs.mkdirSync(dataDir, { recursive: true });
  const safeConfig = clone(config);
  delete safeConfig.execution.botToken;
  delete safeConfig.autoLogin.password;
  fs.writeFileSync(configPath, JSON.stringify(safeConfig, null, 2), "utf8");
}

function getSecrets() {
  return {
    botToken: String(process.env.CAD_BOT_TOKEN || "").trim(),
  };
}

function maskId(value) {
  const text = String(value || "");
  if (text.length <= 8) return text ? "***" : "";
  return `${text.slice(0, 4)}...${text.slice(-4)}`;
}

function purgeExpiredCaohuaSessions() {
  const now = Date.now();
  for (const [sessionId, session] of caohuaSessions.entries()) {
    if (shouldExpireCredentialSession(session, now)) {
      disconnectCaohuaSession(sessionId, "草花渠道凭证已过期");
    }
  }
}

function disconnectCaohuaSession(sessionId, reason = "用户主动断开角色连接") {
  const session = caohuaSessions.get(sessionId);
  if (!session || session.closing) return false;
  session.closing = true;
  session.reconnectLoop?.stop();
  session.reconnectLoop = null;
  stopGatewayResources(session.gateway, new Error(reason));
  session.reconnectSecret = null;
  caohuaSessions.delete(sessionId);
  if (runtime.caohua.activeSession?.sessionId === sessionId) {
    runtime.caohua.activeSession = null;
    runtime.automation = {
      ...runtime.automation,
      sessionId: null,
      running: false,
      inProgress: false,
      nextRunAt: null,
    };
  }
  addLog("session", reason);
  return true;
}

function handleUnexpectedGatewayClose(sessionId, reason = "角色网关连接已断开") {
  const session = caohuaSessions.get(sessionId);
  if (!session || session.closing || session.reconnecting || session.reconnectLoop?.active) return;
  const config = readConfig();
  if (!config.gateway.autoReconnect || !session.reconnectSecret) {
    disconnectCaohuaSession(sessionId, reason);
    return;
  }
  session.reconnecting = true;
  session.resumeAutomation = Boolean(session.gateway?.scheduler?.running);
  stopGatewayResources(session.gateway, new Error(reason));
  session.gateway = null;
  addLog("session", `${reason}，准备自动重连`);
  session.reconnectLoop = new CadReconnectLoop({
    getConfig: () => readConfig().gateway,
    connect: async () => {
      const privateSession = secretVault.open(session.reconnectSecret);
      await connectLoginGateway(readConfig(), privateSession, runtime.caohua.activeSession, {
        startAutomation: session.resumeAutomation,
      });
    },
    onUpdate: (state) => {
      if (runtime.caohua.activeSession?.sessionId !== sessionId) return;
      runtime.caohua.activeSession = {
        ...runtime.caohua.activeSession,
        roleOnline: false,
        reconnect: state,
      };
    },
    onAttemptFailure: (error, state) => {
      addLog("session", `角色网关第 ${state.attempt} 次自动重连失败`, {
        message: error.message,
      });
    },
    onSuccess: (state) => {
      session.reconnecting = false;
      if (runtime.caohua.activeSession?.sessionId === sessionId) {
        runtime.caohua.activeSession.reconnect = {
          ...state,
          active: false,
          reconnectedAt: new Date().toISOString(),
        };
      }
      addLog("session", `角色网关第 ${state.attempt} 次自动重连成功`);
    },
    onExhausted: () => {
      disconnectCaohuaSession(sessionId, "角色网关自动重连次数已用完");
    },
  });
  session.reconnectLoop.lastError = reason;
  session.reconnectLoop.start();
}

function disconnectActiveCaohuaSession(reason) {
  const sessionId = runtime.caohua.activeSession?.sessionId;
  return sessionId ? disconnectCaohuaSession(sessionId, reason) : false;
}

function getActiveAutomationScheduler() {
  purgeExpiredCaohuaSessions();
  const sessionId = runtime.caohua.activeSession?.sessionId;
  const session = sessionId ? caohuaSessions.get(sessionId) : null;
  const scheduler = session?.gateway?.scheduler;
  if (!scheduler) {
    throw new Error("请先完成角色登录，再控制挂机任务");
  }
  return scheduler;
}

function getActiveCaohuaSession(
  missingMessage = "请先完成一次草花渠道登录，再继续当前操作",
) {
  purgeExpiredCaohuaSessions();
  const sessionId = runtime.caohua.activeSession?.sessionId;
  const session = sessionId ? caohuaSessions.get(sessionId) : null;
  if (!session) throw new Error(missingMessage);
  return session;
}

function activateCaohuaSession(privateSession) {
  disconnectActiveCaohuaSession("新的草花登录会话已建立，旧连接已断开");
  const sessionId = crypto.randomUUID();
  const expiresAt = Date.now() + CAOHUA_SESSION_TTL_MS;
  caohuaSessions.set(sessionId, {
    sessionId,
    ...privateSession,
    expiresAt,
    reconnectSecret: secretVault.seal(privateSession),
  });
  runtime.caohua.activeSession = {
    sessionId,
    userId: maskId(privateSession.userId),
    source: privateSession.source,
    expiresAt: new Date(expiresAt).toISOString(),
  };
  addLog("caohua", "草花渠道凭证已就绪", {
    userId: runtime.caohua.activeSession.userId,
    source: privateSession.source,
    expiresAt: runtime.caohua.activeSession.expiresAt,
  });
  return runtime.caohua.activeSession;
}

function addLog(type, message, detail = null) {
  runtime.logs.unshift({
    time: new Date().toISOString(),
    type,
    message,
    detail,
  });
  runtime.logs = runtime.logs.slice(0, 80);
}

function sendJson(res, status, data) {
  res.writeHead(status, {
    "Content-Type": "application/json; charset=utf-8",
    "Cache-Control": "no-store",
  });
  res.end(JSON.stringify(data, null, 2));
}

function sendText(res, status, text, contentType = "text/plain; charset=utf-8") {
  res.writeHead(status, {
    "Content-Type": contentType,
    "Cache-Control": "no-store",
  });
  res.end(text);
}

function readBody(req) {
  return new Promise((resolve, reject) => {
    let raw = "";
    req.on("data", (chunk) => {
      raw += chunk;
      if (raw.length > 1024 * 1024) {
        reject(new Error("Request body is too large"));
        req.destroy();
      }
    });
    req.on("end", () => {
      if (!raw) {
        resolve({});
        return;
      }
      try {
        resolve(JSON.parse(raw));
      } catch (error) {
        reject(error);
      }
    });
    req.on("error", reject);
  });
}

function getMime(filePath) {
  const ext = path.extname(filePath).toLowerCase();
  return {
    ".html": "text/html; charset=utf-8",
    ".css": "text/css; charset=utf-8",
    ".js": "application/javascript; charset=utf-8",
    ".json": "application/json; charset=utf-8",
    ".svg": "image/svg+xml",
    ".png": "image/png",
  }[ext] || "application/octet-stream";
}

function serveStatic(res, url) {
  const pathname = decodeURIComponent(url.pathname === "/" ? "/index.html" : url.pathname);
  const filePath = path.normalize(path.join(rootDir, pathname));
  if (!filePath.startsWith(rootDir)) {
    sendText(res, 403, "Forbidden");
    return;
  }

  fs.readFile(filePath, (error, data) => {
    if (error) {
      sendText(res, 404, "Not found");
      return;
    }
    res.writeHead(200, { "Content-Type": getMime(filePath) });
    res.end(data);
  });
}

function getStatus() {
  purgeExpiredCaohuaSessions();
  const config = readConfig();
  let androidChannels = [];
  let androidChannelError = null;
  try {
    androidChannels = loadAndroidChannelProfiles(config.target.unityProjectPath);
  } catch (error) {
    androidChannelError = error.message;
  }
  return {
    ok: true,
    config,
    runtime,
    androidChannels,
    androidChannelError,
  };
}

function createLoginCommand(source = "web") {
  const config = readConfig();
  runtime.commandSeq += 1;
  runtime.lastCommand = {
    id: `login-${Date.now()}-${runtime.commandSeq}`,
    type: "autoLogin",
    source,
    createdAt: new Date().toISOString(),
    status: "pending",
    autoLogin: clone(config.autoLogin),
    target: clone(config.target),
  };
  addLog("command", "auto-login command created", runtime.lastCommand);
  return runtime.lastCommand;
}

function getAdapter(adapterName) {
  if (adapterName === "mock") {
    return require("./adapters/mockOfficialAdapter");
  }

  if (adapterName === "official") {
    return require("./adapters/officialAdapter");
  }

  throw new Error(`Unknown execution adapter: ${adapterName}`);
}

function canRunProduction(config, credentials) {
  const secrets = getSecrets();
  if (!config.autoLogin.enabled) {
    return { ok: false, message: "autoLogin.enabled is false" };
  }

  if (runtime.lastExecution?.finishedAt) {
    const elapsedSeconds = (Date.now() - Date.parse(runtime.lastExecution.finishedAt)) / 1000;
    if (elapsedSeconds < config.execution.rateLimitSeconds) {
      return {
        ok: false,
        message: `local rate limit hit; retry after ${Math.ceil(
          config.execution.rateLimitSeconds - elapsedSeconds,
        )} seconds`,
      };
    }
  }

  if (config.execution.environment !== "production") {
    return { ok: true };
  }

  if (!config.execution.productionGuard) {
    return { ok: false, message: "productionGuard is required for production" };
  }

  if (config.execution.adapter !== "official") {
    return { ok: true };
  }

  const needsAccountExchange = credentials.username && credentials.password;
  if (needsAccountExchange && !secrets.botToken) {
    return { ok: false, message: "official adapter requires CAD_BOT_TOKEN" };
  }

  if (!config.execution.officialApi.baseUrl) {
    const hasIssuedCredential = credentials.userId && credentials.channelToken;
    if (!hasIssuedCredential) {
      return { ok: false, message: "草花账号密码登录需要配置已授权的登录 API 地址" };
    }
  }

  return { ok: true };
}

function executionPreview(config) {
  return {
    adapter: config.execution.adapter,
    environment: config.execution.environment,
    officialApiBaseUrl: config.execution.officialApi.baseUrl,
    gatewayEnabled: config.gateway.enabled,
    gatewayServerUrl: config.gateway.serverUrl,
    gatewayServerNode: config.gateway.serverNode,
    codeVersion: config.gameplay.codeVersion,
    clientVersion: config.gameplay.clientVersion,
    serverId: config.autoLogin.serverId,
    mode: config.autoLogin.mode,
    roleId: config.autoLogin.roleId,
    roleIndex: config.autoLogin.roleIndex,
    enterGame: config.autoLogin.enterGame,
  };
}

function normalizeCredentials(body) {
  return {
    username: String(body.username || "").trim(),
    password: String(body.password || ""),
    userId: String(body.userId || "").trim(),
    channelToken: String(body.channelToken || "").trim(),
    sdkParam: String(body.sdkParam || "").trim(),
  };
}

function validateCredentials(credentials) {
  const hasPasswordLogin = credentials.username && credentials.password;
  const hasIssuedCredential = credentials.userId && credentials.channelToken;
  if (!hasPasswordLogin && !hasIssuedCredential) {
    throw new Error("请输入草花账号和密码，或输入 SDK 已签发的 userId/token");
  }
}

async function connectLoginGateway(config, privateSession, publicSession, options = {}) {
  if (!config.gateway.enabled) return null;
  if (!privateSession.sdkParam) {
    throw new Error("连接安卓登录服需要本次草花 SDK 的 sdkParam");
  }

  const url = buildLoginWebSocketUrl(config.gateway);
  const privateGatewaySession = await authenticateLoginGateway({
    url,
    userId: privateSession.userId,
    channelToken: privateSession.channelToken,
    platform: "2",
    language: "ANDROID",
    serverNode: config.gateway.serverNode,
    websocketFlag: 1,
    sdkParam: privateSession.sdkParam,
    timeoutSeconds: config.gateway.timeoutSeconds,
  });

  const storedSession = caohuaSessions.get(publicSession.sessionId);
  let privateGameSession = null;
  let bridge = null;
  let protocol = null;
  let keeper = null;
  let scheduler = null;
  let stateStore = null;
  let role = null;
  if (config.autoLogin.enterGame) {
    privateGameSession = await connectGameGateway({
      serverUrl: config.gateway.serverUrl,
      sharedSecret: privateGatewaySession.sharedSecret,
      auth: privateGatewaySession.auth,
      connectIndex: 1,
      timeoutSeconds: config.gateway.timeoutSeconds,
    });
    try {
      bridge = new SprotoBridgeClient({ timeoutMs: config.gateway.timeoutSeconds * 1000 });
      await bridge.start();
      protocol = new CadGameProtocolSession({
        socket: privateGameSession.socket,
        sharedSecret: privateGatewaySession.sharedSecret,
        bridge,
        timeoutSeconds: config.gateway.timeoutSeconds,
      });
      protocol.on("protocolError", (error) => addLog("protocol", error.message));
      stateStore = new CadGameStateStore({
        protocol,
        unityProjectPath: config.target.unityProjectPath,
        onError: (error) => addLog("automation", error.message),
      });
      role = await loginSelectedRole({
        protocol,
        codeVersion: config.gameplay.codeVersion,
        clientVersion: config.gameplay.clientVersion,
        language: config.gameplay.language,
        deviceModel: config.gameplay.deviceModel,
        belongId: config.gameplay.belongId,
        serverNode: config.gateway.serverNode,
        mode: config.autoLogin.mode,
        roleId: config.autoLogin.roleId,
        roleIndex: config.autoLogin.roleIndex,
        sdkParam: privateSession.sdkParam,
        userId: privateSession.userId,
      });
      keeper = new CadOnlineKeeper({
        protocol,
        intervalSeconds: 15,
        onError: (error) => addLog("heartbeat", error.message),
        onUpdate: (health) => {
          if (runtime.caohua.activeSession?.sessionId === publicSession.sessionId) {
            runtime.caohua.activeSession.health = health;
          }
        },
        onUnhealthy: () => {
          handleUnexpectedGatewayClose(publicSession.sessionId, "角色网关连续 3 次心跳失败");
        },
      });
      keeper.start();
      scheduler = new CadTaskScheduler({
        protocol,
        stateStore,
        config: config.automation,
        onUpdate: (state) => {
          if (runtime.caohua.activeSession?.sessionId === publicSession.sessionId) {
            runtime.automation = { sessionId: publicSession.sessionId, ...state };
          }
        },
      });
      runtime.automation = {
        sessionId: publicSession.sessionId,
        ...scheduler.snapshot(),
      };
      if (options.startAutomation ?? config.automation.enabled) {
        await scheduler.start(true);
      }
    } catch (error) {
      protocol?.close(error);
      keeper?.stop();
      scheduler?.stop();
      stateStore?.stop();
      await bridge?.stop().catch(() => {});
      try {
        privateGameSession.socket.close();
      } catch {
        // Connection cleanup should not hide the protocol error.
      }
      throw error;
    }
  }
  const gatewayResources = {
    ...privateGatewaySession,
    game: privateGameSession,
    bridge,
    protocol,
    keeper,
    scheduler,
    stateStore,
  };
  if (
    !storedSession ||
    storedSession.closing ||
    caohuaSessions.get(publicSession.sessionId) !== storedSession
  ) {
    stopGatewayResources(
      gatewayResources,
      new Error("草花渠道会话在网关连接完成前已失效"),
    );
    throw new Error("草花渠道会话在网关连接完成前已失效，请重新登录");
  }
  if (storedSession) {
    storedSession.gateway = gatewayResources;
    if (role?.selectedRole && role.loginResult) {
      const connectedAt = Date.now();
      promoteToPersistentSession(storedSession, connectedAt);
      runtime.caohua.activeSession = {
        ...runtime.caohua.activeSession,
        roleOnline: true,
        connectedAt: new Date(connectedAt).toISOString(),
        expiresAt: null,
        health: keeper.snapshot(),
        reconnect: { active: false },
      };
      protocol.on("closed", () => {
        handleUnexpectedGatewayClose(publicSession.sessionId);
      });
    }
  }
  const auth = privateGatewaySession.auth;
  return {
    connected: true,
    accountId: maskId(auth.accountId),
    uid: auth.uid,
    serverName: auth.serverName,
    gatewayHost: auth.gatewayHost,
    gatewayPort: auth.gatewayPort,
    gameConnected: Boolean(privateGameSession),
    roleAuthenticated: Boolean(role?.selectedRole && role.loginResult),
    role: role?.selectedRole || null,
    roleCount: role?.roleCount || 0,
  };
}

async function executeAutoLogin(command, credentials) {
  const config = readConfig();
  purgeExpiredCaohuaSessions();
  validateCredentials(credentials);
  const guard = canRunProduction(config, credentials);
  if (!guard.ok) {
    command.status = "blocked";
    runtime.lastExecution = {
      commandId: command.id,
      status: "blocked",
      message: guard.message,
      finishedAt: new Date().toISOString(),
    };
    addLog("guard", guard.message, runtime.lastExecution);
    return runtime.lastExecution;
  }

  if (config.execution.dryRun) {
    command.status = "dryRun";
    runtime.lastExecution = {
      commandId: command.id,
      status: "dryRun",
      message: "dryRun 已启用；凭证字段校验通过，未发送草花登录请求",
      config: executionPreview(config),
      credentialMode: credentials.userId ? "issuedToken" : "accountPassword",
      finishedAt: new Date().toISOString(),
    };
    addLog("dryRun", runtime.lastExecution.message, runtime.lastExecution);
    return runtime.lastExecution;
  }

  command.status = "running";
  addLog("execute", "auto-login execution started", { commandId: command.id });

  const adapter = getAdapter(config.execution.adapter);
  const result = await adapter.loginAuto(config, {
    botToken: getSecrets().botToken,
    credentials,
  });
  const privateSession = result.privateSession;
  delete result.privateSession;
  if (result.ok && privateSession) {
    result.channelSession = activateCaohuaSession(privateSession);
    try {
      const gateway = await connectLoginGateway(config, privateSession, result.channelSession);
      if (gateway) {
        result.gateway = gateway;
        result.status = gateway.roleAuthenticated
          ? "roleAuthenticated"
          : gateway.gameConnected
            ? "gameGatewayAuthenticated"
            : "loginGatewayAuthenticated";
        result.message = gateway.gameConnected
          ? "草花渠道、游戏登录服和角色网关认证均已完成"
          : "草花渠道登录和游戏登录服认证均已完成";
        if (gateway.roleAuthenticated) {
          result.message = "Android Caohua account and selected role login completed";
        }
      }
    } catch (error) {
      result.ok = false;
      result.status = "gatewayFailed";
      result.message = error.message;
    }
  }
  command.status = result.status || (result.ok ? "completed" : "failed");
  runtime.lastExecution = {
    commandId: command.id,
    ...result,
    finishedAt: new Date().toISOString(),
  };
  addLog("execute", runtime.lastExecution.message || "auto-login execution finished", runtime.lastExecution);
  return runtime.lastExecution;
}

async function handleApi(req, res, url) {
  if (url.pathname === "/api/server-directory" && req.method === "GET") {
    const environment = url.searchParams.get("environment") || "release";
    const directory = await fetchOfficialServerDirectory({ environment });
    sendJson(res, 200, { ok: true, ...directory });
    return;
  }

  if (url.pathname === "/api/account-servers" && req.method === "GET") {
    const session = getActiveCaohuaSession(
      "请先完成一次草花渠道登录，再查询账号角色区服",
    );
    const environment = url.searchParams.get("environment") || "release";
    const [roleServers, directory] = await Promise.all([
      fetchAndroidRoleServers({ accountId: session.userId }),
      fetchOfficialServerDirectory({ environment }),
    ]);
    const servers = matchRoleServers(directory.servers, roleServers.gameNodes);
    sendJson(res, 200, {
      ok: true,
      environment,
      servers,
      unmatchedGameNodes: roleServers.gameNodes.filter(
        (node) => !servers.some((server) => server.serverNode === node),
      ),
      queue: {
        waitTime: roleServers.waitTime,
        queueNum: roleServers.queueNum,
        gameNode: roleServers.queueGameNode,
      },
    });
    return;
  }

  if (url.pathname === "/api/status" && req.method === "GET") {
    sendJson(res, 200, getStatus());
    return;
  }

  if (url.pathname === "/api/config" && req.method === "GET") {
    sendJson(res, 200, readConfig());
    return;
  }

  if (url.pathname === "/api/config" && req.method === "PUT") {
    const current = readConfig();
    const next = await readBody(req);
    const nextExecution = next.execution || {};
    const config = {
      ...current,
      ...next,
      target: { ...current.target, ...(next.target || {}) },
      server: { ...current.server, ...(next.server || {}) },
      execution: {
        ...current.execution,
        ...nextExecution,
        officialApi: {
          ...current.execution.officialApi,
          ...(nextExecution.officialApi || {}),
        },
      },
      gateway: { ...current.gateway, ...(next.gateway || {}) },
      gameplay: { ...current.gameplay, ...(next.gameplay || {}) },
      androidChannel: {
        ...current.androidChannel,
        ...(next.androidChannel || {}),
      },
      autoLogin: { ...current.autoLogin, ...(next.autoLogin || {}) },
      automation: {
        ...current.automation,
        ...(next.automation || {}),
        tasks: {
          ...current.automation.tasks,
          ...(next.automation?.tasks || {}),
        },
        wipeBattle: {
          ...current.automation.wipeBattle,
          ...(next.automation?.wipeBattle || {}),
        },
      },
    };
    writeConfig(config);
    const activeSessionId = runtime.caohua.activeSession?.sessionId;
    const activeScheduler = activeSessionId
      ? caohuaSessions.get(activeSessionId)?.gateway?.scheduler
      : null;
    if (activeScheduler) activeScheduler.config = config.automation;
    addLog("config", "auto-login config saved");
    sendJson(res, 200, { ok: true, config });
    return;
  }

  if (url.pathname === "/api/login/request" && req.method === "POST") {
    const credentials = normalizeCredentials(await readBody(req));
    const command = createLoginCommand("web");
    const execution = await executeAutoLogin(command, credentials);
    sendJson(res, 200, { ok: true, command, execution });
    return;
  }

  if (url.pathname === "/api/session/disconnect" && req.method === "POST") {
    const disconnected = disconnectActiveCaohuaSession("用户主动断开角色连接");
    sendJson(res, 200, { ok: true, disconnected });
    return;
  }

  if (url.pathname === "/api/session/connect" && req.method === "POST") {
    const session = getActiveCaohuaSession(
      "请先完成一次草花渠道登录，再连接所选区服",
    );
    const publicSession = runtime.caohua.activeSession;
    const config = readConfig();
    const gateway = await connectExistingSession({
      config,
      session,
      publicSession,
      connectLoginGateway,
    });
    addLog("session", "已使用当前草花凭证连接所选区服", {
      serverId: config.autoLogin.serverId,
      serverNode: config.gateway.serverNode,
      roleAuthenticated: gateway.roleAuthenticated,
    });
    sendJson(res, 200, { ok: true, gateway });
    return;
  }

  if (url.pathname === "/api/automation/start" && req.method === "POST") {
    const scheduler = getActiveAutomationScheduler();
    const state = await scheduler.start(true);
    addLog("automation", "挂机任务已启动");
    sendJson(res, 200, { ok: true, state });
    return;
  }

  if (url.pathname === "/api/automation/stop" && req.method === "POST") {
    const state = getActiveAutomationScheduler().stop();
    addLog("automation", "挂机任务已停止");
    sendJson(res, 200, { ok: true, state });
    return;
  }

  if (url.pathname === "/api/automation/run" && req.method === "POST") {
    const result = await getActiveAutomationScheduler().runOnce("web");
    addLog("automation", "挂机任务已立即执行", {
      status: result.status,
      results: result.results,
    });
    sendJson(res, 200, { ok: true, result });
    return;
  }

  sendJson(res, 404, { ok: false, error: "Unknown API" });
}

async function handleRequest(req, res) {
  try {
    const url = new URL(req.url, "http://127.0.0.1");
    if (url.pathname.startsWith("/api/")) {
      await handleApi(req, res, url);
      return;
    }
    serveStatic(res, url);
  } catch (error) {
    addLog("error", error.message);
    sendJson(res, 500, { ok: false, error: error.message });
  }
}

const config = readConfig();
const host = process.env.CAD_AUTO_BOT_HOST || config.server.host || "127.0.0.1";
const port = Number(process.env.CAD_AUTO_BOT_PORT || config.server.port || 17830);
runtime.server = { host, port };

const server = http.createServer(handleRequest);
server.listen(port, host, () => {
  addLog("server", `CAD Auto Bot started at http://${host}:${port}`);
  console.log(`CAD Auto Bot: http://${host}:${port}`);
});

let shuttingDown = false;
function shutdown() {
  if (shuttingDown) return;
  shuttingDown = true;
  for (const sessionId of [...caohuaSessions.keys()]) {
    disconnectCaohuaSession(sessionId, "本地挂机服务正在退出");
  }
  secretVault.clear();
  server.close(() => process.exit(0));
}

process.once("SIGINT", shutdown);
process.once("SIGTERM", shutdown);
