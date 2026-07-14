const test = require("node:test");
const assert = require("node:assert/strict");
const {
  buildServerDirectoryUrl,
  fetchOfficialServerDirectory,
  parseServerDirectory,
} = require("../protocol/cadServerDirectory");

function server(overrides = {}) {
  return {
    WsPort: 10001,
    Id: "9007199254740993",
    Ip: "192.168.0.4",
    Url: "wss://jxkl-xcx-login.caohua.com/ws",
    Name: "game42",
    Status: 2,
    ShowName: "测试区服",
    OpenTime: 1738807200,
    AreaId: 1,
    AreaName: "测试大区",
    ...overrides,
  };
}

test("区服目录保留长整型 ID 并映射登录网关字段", () => {
  const [item] = parseServerDirectory({ Data: [server({ OpenTime: 1738807200.728 })] });
  assert.equal(item.id, "9007199254740993");
  assert.equal(item.serverNode, "game42");
  assert.equal(item.serverUrl, "wss://jxkl-xcx-login.caohua.com/ws");
  assert.equal(item.serverIp, "192.168.0.4");
  assert.equal(item.port, 10001);
  assert.equal(item.openTime, "1738807200");
  assert.equal(item.selectable, true);
});

test("维护和未开放区服只展示但不可选择", () => {
  const items = parseServerDirectory({
    Data: [server({ Id: 1, Status: 3 }), server({ Id: 2, Status: 4 })],
  });
  assert.deepEqual(items.map((item) => item.selectable), [false, false]);
});

test("只允许客户端已有的固定区服目录环境", () => {
  assert.equal(
    buildServerDirectoryUrl("release"),
    "https://jxkl-xcx-res.caohua.com/weixin/release/mini_serverlist_0.json",
  );
  assert.throws(() => buildServerDirectoryUrl("https://evil.example"), /只允许/);
});

test("请求官方目录时解析响应并返回固定来源", async () => {
  const result = await fetchOfficialServerDirectory({
    environment: "develop",
    fetchImpl: async () => ({
      ok: true,
      status: 200,
      json: async () => ({ Data: [server()] }),
    }),
  });
  assert.equal(result.environment, "develop");
  assert.equal(result.servers.length, 1);
  assert.match(result.source, /\/develop\/mini_serverlist_0\.json$/);
});

test("拒绝非 wss 入口和异常目录结构", () => {
  assert.throws(() => parseServerDirectory({}), /Data/);
  assert.throws(
    () => parseServerDirectory({ Data: [server({ Url: "http://example.com" })] }),
    /wss/,
  );
});
