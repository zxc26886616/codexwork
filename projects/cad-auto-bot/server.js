const http = require("http");
const fs = require("fs");
const path = require("path");
const { URL } = require("url");

const rootDir = __dirname;
const dataDir = path.join(rootDir, "data");
const configPath = path.join(dataDir, "config.json");

const defaultConfig = {
  target: {
    unityProjectPath: "D:\\code\\cad_client\\cadclient3.0",
    clientName: "cadclient3.0",
  },
  server: {
    host: "127.0.0.1",
    port: 17830,
  },
  execution: {
    adapter: "mock",
    environment: "production",
    productionGuard: true,
    botToken: "",
    rateLimitSeconds: 10,
    dryRun: true,
    officialApi: {
      baseUrl: "",
      loginPath: "/bot/login",
      enterGamePath: "/bot/enter-game",
      timeoutSeconds: 15,
    },
  },
  autoLogin: {
    enabled: true,
    mode: "lastRole",
    account: "",
    password: "",
    serverId: "",
    roleId: "",
    roleIndex: 0,
    enterGame: true,
    delaySeconds: 2,
    retryTimes: 3,
    retryIntervalSeconds: 5,
  },
};

const runtime = {
  startedAt: new Date().toISOString(),
  lastCommand: null,
  lastExecution: null,
  commandSeq: 0,
  logs: [],
};

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
  return {
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
    autoLogin: { ...defaultConfig.autoLogin, ...(saved.autoLogin || {}) },
  };
}

function writeConfig(config) {
  fs.mkdirSync(dataDir, { recursive: true });
  fs.writeFileSync(configPath, JSON.stringify(config, null, 2), "utf8");
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
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET,POST,PUT,OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type",
  });
  res.end(JSON.stringify(data, null, 2));
}

function sendText(res, status, text, contentType = "text/plain; charset=utf-8") {
  res.writeHead(status, {
    "Content-Type": contentType,
    "Access-Control-Allow-Origin": "*",
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
  return {
    ok: true,
    config: readConfig(),
    runtime,
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

function canRunProduction(config) {
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

  if (!config.execution.botToken) {
    return { ok: false, message: "official adapter requires botToken" };
  }

  if (!config.execution.officialApi.baseUrl) {
    return { ok: false, message: "official adapter requires officialApi.baseUrl" };
  }

  return { ok: true };
}

function executionPreview(config) {
  return {
    adapter: config.execution.adapter,
    environment: config.execution.environment,
    officialApiBaseUrl: config.execution.officialApi.baseUrl,
    account: config.autoLogin.account,
    serverId: config.autoLogin.serverId,
    mode: config.autoLogin.mode,
    roleId: config.autoLogin.roleId,
    roleIndex: config.autoLogin.roleIndex,
    enterGame: config.autoLogin.enterGame,
  };
}

async function executeAutoLogin(command) {
  const config = readConfig();
  const guard = canRunProduction(config);
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
      message: "dryRun is enabled; no official API request was sent",
      config: executionPreview(config),
      finishedAt: new Date().toISOString(),
    };
    addLog("dryRun", runtime.lastExecution.message, runtime.lastExecution);
    return runtime.lastExecution;
  }

  command.status = "running";
  addLog("execute", "auto-login execution started", { commandId: command.id });

  const adapter = getAdapter(config.execution.adapter);
  const result = await adapter.loginAuto(config);
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
  if (req.method === "OPTIONS") {
    sendJson(res, 204, {});
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
      autoLogin: { ...current.autoLogin, ...(next.autoLogin || {}) },
    };
    writeConfig(config);
    addLog("config", "auto-login config saved");
    sendJson(res, 200, { ok: true, config });
    return;
  }

  if (url.pathname === "/api/login/request" && req.method === "POST") {
    const command = createLoginCommand("web");
    const execution = await executeAutoLogin(command);
    sendJson(res, 200, { ok: true, command, execution });
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

const server = http.createServer(handleRequest);
server.listen(port, host, () => {
  addLog("server", `CAD Auto Bot started at http://${host}:${port}`);
  console.log(`CAD Auto Bot: http://${host}:${port}`);
});
