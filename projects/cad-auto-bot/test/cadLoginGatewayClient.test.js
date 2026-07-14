const test = require("node:test");
const assert = require("node:assert/strict");
const {
  authenticateLoginGateway,
  buildLoginWebSocketUrl,
} = require("../protocol/cadLoginGatewayClient");
const { dhExchange } = require("../protocol/cadGatewayCrypto");

function authResponse() {
  const fields = [
    "account-1",
    "7",
    "game-host",
    "10001",
    "127.0.0.1",
    "game1",
    "42",
    "session-value",
  ];
  const payload = Buffer.from(
    fields.map((field) => Buffer.from(field).toString("base64")).join("@"),
  ).toString("base64");
  return `200 ${payload}\n`;
}

class FakeSocket {
  constructor() {
    this.listeners = new Map();
    this.sent = [];
    this.serverKey = dhExchange(Buffer.from("090a0b0c0d0e0f10", "hex"));
    queueMicrotask(() => {
      this.emit("message", Buffer.from("1112131415161718", "hex").toString("base64"));
    });
  }

  addEventListener(name, listener) {
    const listeners = this.listeners.get(name) || [];
    listeners.push(listener);
    this.listeners.set(name, listeners);
  }

  emit(name, data) {
    for (const listener of this.listeners.get(name) || []) {
      listener({ data });
    }
  }

  send(value) {
    this.sent.push(Buffer.from(value).toString("utf8"));
    if (this.sent.length === 1) {
      queueMicrotask(() => this.emit("message", this.serverKey.toString("base64")));
    } else if (this.sent.length === 3) {
      queueMicrotask(() => this.emit("message", authResponse()));
    }
  }

  close() {}
}

test("登录服客户端完成与 Unity 一致的三段握手", async () => {
  let socket;
  const result = await authenticateLoginGateway(
    {
      url: "wss://login.example/ws",
      userId: "12345",
      channelToken: "token-value",
      platform: "2",
      language: "ANDROID",
      serverNode: "game1",
      websocketFlag: 1,
      sdkParam: "sdk-base64",
      clientKey: Buffer.from("0102030405060708", "hex"),
      timeoutSeconds: 1,
    },
    {
      socketFactory() {
        socket = new FakeSocket();
        return socket;
      },
      tokenDelayMs: 0,
    },
  );

  assert.equal(socket.sent[0], "mE4UGCgyNe0=\n");
  assert.equal(socket.sent[1], "LKvn0RA5jNM=\n");
  assert.equal(socket.sent.length, 3);
  assert.equal(result.auth.uid, 42);
  assert.equal(result.auth.serverName, "game1");
  assert.equal(result.sharedSecret.toString("hex"), "78ab0d3f882c1787");
});

test("登录 WebSocket URL 复刻客户端 host/port/rand 查询参数", () => {
  const url = buildLoginWebSocketUrl(
    {
      serverUrl: "wss://login.example/ws?source=android",
      serverIp: "10.0.0.8",
      port: 10001,
    },
    () => 123456,
  );
  assert.equal(
    url,
    "wss://login.example/ws?source=android&host=10.0.0.8&port=10001&rand=123456",
  );
});

test("登录 WebSocket URL 拒绝非 WebSocket 协议", () => {
  assert.throws(
    () => buildLoginWebSocketUrl({ serverUrl: "https://login.example/ws" }),
    /ws:\/\//,
  );
});
