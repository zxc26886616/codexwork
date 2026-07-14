const crypto = require("crypto");

const ANDROID_ROLE_SERVER_URL = "https://jjkl-xcx-android-login.caohua.com/queryHaveRole";
const ROLE_SERVER_SIGNING_SALT = "MBfnrvb8xBdHQnFNF0FHEi8nZM6k9ENu";

function buildRoleServerSignature(accountId) {
  const normalized = String(accountId || "").trim();
  if (!normalized) throw new Error("查询已有角色区服需要渠道 userId");
  return crypto
    .createHash("md5")
    .update(`accountId=${normalized}&year=2024${ROLE_SERVER_SIGNING_SALT}`, "utf8")
    .digest("hex");
}

function parseRoleServerResponse(payload) {
  if (!payload || !Array.isArray(payload.data)) {
    throw new Error("已有角色区服响应缺少 data 数组");
  }
  const seen = new Set();
  const gameNodes = [];
  for (const value of payload.data) {
    const node = String(value || "").trim();
    if (!/^game\d+$/.test(node) || seen.has(node)) continue;
    seen.add(node);
    gameNodes.push(node);
  }
  return {
    code: Number(payload.code || 0),
    gameNodes,
    waitTime: Math.max(0, Number(payload.waitTime || 0)),
    queueNum: Math.max(0, Number(payload.queueNum || 0)),
    queueGameNode: String(payload.gameNode || "").trim(),
  };
}

async function fetchAndroidRoleServers({
  accountId,
  fetchImpl = globalThis.fetch,
  timeoutMs = 10000,
  randomValue = Date.now(),
} = {}) {
  if (typeof fetchImpl !== "function") throw new Error("当前 Node.js 环境不支持 fetch");
  const normalized = String(accountId || "").trim();
  const url = new URL(ANDROID_ROLE_SERVER_URL);
  url.searchParams.set("accountId", normalized);
  url.searchParams.set("sign", buildRoleServerSignature(normalized));
  url.searchParams.set("rand", String(randomValue));

  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), timeoutMs);
  try {
    const response = await fetchImpl(url, {
      headers: { Accept: "application/json" },
      signal: controller.signal,
    });
    if (!response.ok) throw new Error(`已有角色区服请求失败：HTTP ${response.status}`);
    return parseRoleServerResponse(await response.json());
  } finally {
    clearTimeout(timer);
  }
}

function matchRoleServers(directory, gameNodes) {
  const wanted = new Set(gameNodes || []);
  return (directory || []).filter((server) => wanted.has(server.serverNode));
}

module.exports = {
  ANDROID_ROLE_SERVER_URL,
  buildRoleServerSignature,
  fetchAndroidRoleServers,
  matchRoleServers,
  parseRoleServerResponse,
};
