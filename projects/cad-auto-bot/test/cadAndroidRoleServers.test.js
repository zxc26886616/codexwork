const test = require("node:test");
const assert = require("node:assert/strict");
const {
  ANDROID_ROLE_SERVER_URL,
  buildRoleServerSignature,
  fetchAndroidRoleServers,
  matchRoleServers,
  parseRoleServerResponse,
} = require("../protocol/cadAndroidRoleServers");

test("已有角色区服签名与 Android 客户端规则一致", () => {
  assert.equal(buildRoleServerSignature("user-123"), "26166a5dedeb1ac6306946d1e0e30dc3");
});

test("已有角色区服响应过滤异常和重复节点", () => {
  assert.deepEqual(
    parseRoleServerResponse({
      code: 0,
      data: ["game908", "game908", "bad", "game12"],
      waitTime: 10,
      queueNum: 2,
      gameNode: "game908",
    }),
    {
      code: 0,
      gameNodes: ["game908", "game12"],
      waitTime: 10,
      queueNum: 2,
      queueGameNode: "game908",
    },
  );
});

test("已有角色节点只与官方目录匹配，不凭空生成入口", () => {
  const directory = [
    { id: "908", serverNode: "game908" },
    { id: "909", serverNode: "game909" },
  ];
  assert.deepEqual(matchRoleServers(directory, ["game908", "game999"]), [directory[0]]);
});

test("已有角色区服请求固定使用 Android 客户端域名且不返回账号", async () => {
  let requestedUrl;
  const result = await fetchAndroidRoleServers({
    accountId: "user-123",
    randomValue: 123,
    fetchImpl: async (url) => {
      requestedUrl = url;
      return { ok: true, status: 200, json: async () => ({ code: 0, data: ["game908"] }) };
    },
  });
  assert.equal(requestedUrl.origin + requestedUrl.pathname, ANDROID_ROLE_SERVER_URL);
  assert.equal(requestedUrl.searchParams.get("accountId"), "user-123");
  assert.equal(requestedUrl.searchParams.get("rand"), "123");
  assert.deepEqual(result.gameNodes, ["game908"]);
  assert.equal("accountId" in result, false);
});
