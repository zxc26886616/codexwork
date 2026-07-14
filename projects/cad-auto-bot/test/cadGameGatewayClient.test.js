const test = require("node:test");
const assert = require("node:assert/strict");
const { connectGameGateway, readFrame } = require("../protocol/cadGameGatewayClient");
const { framePackage } = require("../protocol/cadGatewayCrypto");

class FakeGameSocket {
  constructor() {
    this.listeners = new Map();
    this.sent = [];
    this.closed = false;
    queueMicrotask(() => this.emit("open"));
  }

  addEventListener(name, listener) {
    const listeners = this.listeners.get(name) || [];
    listeners.push(listener);
    this.listeners.set(name, listeners);
  }

  emit(name, data) {
    for (const listener of this.listeners.get(name) || []) listener({ data });
  }

  send(value) {
    this.sent.push(Buffer.from(value));
    queueMicrotask(() => this.emit("message", framePackage(Buffer.from("200 OK\n"))));
  }

  close() {
    this.closed = true;
  }
}

test("角色网关发送二字节包头的重定向认证并保持连接", async () => {
  let socket;
  const session = await connectGameGateway(
    {
      serverUrl: "wss://login.example/ws",
      sharedSecret: Buffer.from("78ab0d3f882c1787", "hex"),
      auth: {
        uid: 42,
        serverName: "game1",
        subId: 7,
        serverIp: "10.0.0.9",
        gatewayPort: 10002,
      },
      timeoutSeconds: 1,
    },
    {
      socketFactory() {
        socket = new FakeGameSocket();
        return socket;
      },
      now: () => 123456,
    },
  );

  assert.equal(
    session.url,
    "wss://login.example/ws?host=10.0.0.9&port=10002&rand=123456",
  );
  assert.equal(socket.sent[0].readUInt16BE(0), socket.sent[0].length - 2);
  assert.match(socket.sent[0].subarray(2).toString("utf8"), /^NDI=@Z2FtZTE=#Nw==:1:/);
  assert.equal(session.connectIndex, 1);
  assert.equal(socket.closed, false);
});

test("角色网关响应必须是完整二字节长度帧", () => {
  assert.throws(() => readFrame(Buffer.from([0, 5, 1, 2])), /不完整/);
});
