const DIRECTORY_ROOT = "https://jxkl-xcx-res.caohua.com";
const ALLOWED_ENVIRONMENTS = new Set(["release", "develop"]);
const SELECTABLE_STATUSES = new Set([0, 1, 2]);

function normalizeIntegerString(value, fieldName) {
  const text = String(value ?? "").trim();
  if (!/^\d+$/.test(text)) {
    throw new Error(`区服目录字段 ${fieldName} 不是有效整数`);
  }
  return text;
}

function normalizeOptionalIntegerString(value, fieldName) {
  if (typeof value === "number" && Number.isFinite(value) && value >= 0) {
    return String(Math.floor(value));
  }
  return normalizeIntegerString(String(value ?? "").trim() || "0", fieldName);
}

function normalizeServerInfo(raw) {
  if (!raw || typeof raw !== "object") {
    throw new Error("区服目录包含无效条目");
  }
  const status = Number(raw.Status);
  const port = Number(raw.WsPort);
  const url = String(raw.Url || "").trim();
  const parsedUrl = new URL(url);
  if (parsedUrl.protocol !== "wss:") throw new Error("区服登录入口必须使用 wss");
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    throw new Error("区服目录包含无效端口");
  }
  if (!Number.isInteger(status) || status < 0 || status > 5) {
    throw new Error("区服目录包含未知状态");
  }
  return {
    id: normalizeIntegerString(raw.Id, "Id"),
    areaId: normalizeOptionalIntegerString(raw.AreaId, "AreaId"),
    openTime: normalizeOptionalIntegerString(raw.OpenTime, "OpenTime"),
    areaName: String(raw.AreaName || "").trim(),
    showName: String(raw.ShowName || "").trim(),
    serverNode: String(raw.Name || "").trim(),
    serverIp: String(raw.Ip || "").trim(),
    serverUrl: url,
    port,
    status,
    selectable: SELECTABLE_STATUSES.has(status),
  };
}

function parseServerDirectory(payload) {
  if (!payload || !Array.isArray(payload.Data)) {
    throw new Error("草花区服目录响应缺少 Data 数组");
  }
  return payload.Data.map(normalizeServerInfo).sort((left, right) => {
    if (left.status !== right.status) return left.status - right.status;
    const leftId = BigInt(left.id);
    const rightId = BigInt(right.id);
    return leftId === rightId ? 0 : rightId > leftId ? 1 : -1;
  });
}

function buildServerDirectoryUrl(environment) {
  if (!ALLOWED_ENVIRONMENTS.has(environment)) {
    throw new Error("区服目录环境只允许 release 或 develop");
  }
  return `${DIRECTORY_ROOT}/weixin/${environment}/mini_serverlist_0.json`;
}

async function fetchOfficialServerDirectory({
  environment = "release",
  fetchImpl = globalThis.fetch,
  timeoutMs = 10000,
} = {}) {
  if (typeof fetchImpl !== "function") throw new Error("当前 Node.js 环境不支持 fetch");
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const source = buildServerDirectoryUrl(environment);
    const response = await fetchImpl(source, {
      headers: { Accept: "application/json" },
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`草花区服目录请求失败：HTTP ${response.status}`);
    return { environment, source, servers: parseServerDirectory(await response.json()) };
  } finally {
    clearTimeout(timer);
  }
}

module.exports = {
  buildServerDirectoryUrl,
  fetchOfficialServerDirectory,
  normalizeServerInfo,
  parseServerDirectory,
};
