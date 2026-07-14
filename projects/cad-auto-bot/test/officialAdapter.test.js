const test = require("node:test");
const assert = require("node:assert/strict");
const http = require("node:http");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { once } = require("node:events");
const { loginAuto } = require("../adapters/officialAdapter");
const { decodeAndroidSdkParam } = require("../protocol/cadAndroidSdkParam");

function createChannelFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cad-adapter-channel-"));
  const configDir = path.join(root, "Config", "Json");
  fs.mkdirSync(configDir, { recursive: true });
  fs.writeFileSync(path.join(configDir, "Channel.json"), JSON.stringify({
    mDataMap: {
      2: {
        ID: 2,
        appId: "1214",
        channelId: "1017",
        channelApplyId: "apply-2",
        marketType1: 29,
      },
    },
  }));
  return root;
}

function createConfig(baseUrl) {
  return {
    target: { clientName: "CAD Client 3.0" },
    execution: {
      officialApi: {
        baseUrl,
        loginPath: "/caohua/login",
        timeoutSeconds: 2,
      },
    },
    autoLogin: {},
  };
}

test("草花账号密码只发送给已配置的登录代理", async (t) => {
  let received = null;
  const server = http.createServer((req, res) => {
    let raw = "";
    req.on("data", (chunk) => {
      raw += chunk;
    });
    req.on("end", () => {
      received = {
        authorization: req.headers.authorization,
        body: JSON.parse(raw),
      };
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ data: { userId: "grass-user-1", token: "channel-token" } }));
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => server.close());

  const address = server.address();
  const result = await loginAuto(createConfig(`http://127.0.0.1:${address.port}`), {
    botToken: "internal-bot-token",
    credentials: { username: "player", password: "one-time-password" },
  });

  assert.equal(result.ok, true);
  assert.equal(received.authorization, "Bearer internal-bot-token");
  assert.equal(received.body.username, "player");
  assert.equal(received.body.password, "one-time-password");
  assert.deepEqual(result.privateSession, {
    userId: "grass-user-1",
    channelToken: "channel-token",
    sdkParam: "",
    source: "accountPassword",
  });
  assert.equal(JSON.stringify(result).includes("one-time-password"), false);
});

test("SDK 已签发的 userId/token 可直接进入本机内存会话", async () => {
  const result = await loginAuto(createConfig(""), {
    credentials: { userId: "grass-user-2", channelToken: "issued-token" },
  });

  assert.equal(result.ok, true);
  assert.equal(result.status, "channelAuthenticated");
  assert.deepEqual(result.privateSession, {
    userId: "grass-user-2",
    channelToken: "issued-token",
    sdkParam: undefined,
    source: "issuedToken",
  });
});

test("登录代理响应必须包含 userId/token", async (t) => {
  const server = http.createServer((req, res) => {
    req.resume();
    req.on("end", () => {
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({ ok: true }));
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => server.close());

  const address = server.address();
  const result = await loginAuto(createConfig(`http://127.0.0.1:${address.port}`), {
    botToken: "internal-bot-token",
    credentials: { username: "player", password: "password" },
  });

  assert.equal(result.ok, false);
  assert.match(result.message, /userId\/token/);
});

test("登录代理返回 extensionJson 时按所选客户端渠道生成 sdkParam", async (t) => {
  let received = null;
  const server = http.createServer((req, res) => {
    let raw = "";
    req.on("data", (chunk) => { raw += chunk; });
    req.on("end", () => {
      received = JSON.parse(raw);
      res.writeHead(200, { "Content-Type": "application/json" });
      res.end(JSON.stringify({
        userId: "grass-user-3",
        token: "channel-token-3",
        extensionJson: '{"device":"android"}',
      }));
    });
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  t.after(() => server.close());

  const address = server.address();
  const config = createConfig(`http://127.0.0.1:${address.port}`);
  config.target.unityProjectPath = createChannelFixture();
  config.androidChannel = { configId: 2 };
  const result = await loginAuto(config, {
    botToken: "internal-bot-token",
    credentials: { username: "player", password: "password" },
  });

  assert.deepEqual(received.androidChannel, {
    id: 2,
    appId: "1214",
    channelId: "1017",
    channelApplyId: "apply-2",
    marketType: 29,
  });
  assert.deepEqual(decodeAndroidSdkParam(result.privateSession.sdkParam), {
    appId: "1214",
    channelId: "1017",
    channelApplyId: "apply-2",
    extensionJson: '{"device":"android"}',
  });
});
