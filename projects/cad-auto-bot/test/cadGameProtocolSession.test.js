const test = require("node:test");
const assert = require("node:assert/strict");
const { CadGameProtocolSession } = require("../protocol/cadGameProtocolSession");
const { desDecode, desEncode, framePackage } = require("../protocol/cadGatewayCrypto");

class FakeSocket {
  constructor() {
    this.listeners = new Map();
    this.sent = [];
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
  }
}

test("game protocol session matches Unity encryption and session framing", async () => {
  const socket = new FakeSocket();
  const secret = Buffer.from("78ab0d3f882c1787", "hex");
  const bridge = {
    async encodeGate(type, fields, session, gateSession) {
      assert.equal(type, "Role_GetRoleList.request");
      assert.deepEqual(fields, { codeVersion: "1.0.11" });
      assert.equal(session, 1);
      assert.equal(gateSession, 2);
      return { packedBase64: Buffer.from("gate-request").toString("base64") };
    },
    async decodeGate(packedBase64) {
      assert.equal(Buffer.from(packedBase64, "base64").toString(), "gate-response");
      return { outerSession: 2, messages: [{ tag: 1 }] };
    },
  };
  const session = new CadGameProtocolSession({
    socket,
    sharedSecret: secret,
    bridge,
    timeoutSeconds: 1,
  });
  const responsePromise = session.send("Role_GetRoleList.request", {
    codeVersion: "1.0.11",
  });
  await new Promise((resolve) => setImmediate(resolve));

  const sent = socket.sent[0];
  assert.equal(sent.readUInt16BE(0), sent.length - 2);
  assert.equal(sent.subarray(sent.length - 4).readUInt32BE(0), 1);
  assert.equal(
    desDecode(secret, sent.subarray(2, sent.length - 4)).toString(),
    "gate-request",
  );

  const encryptedResponse = desEncode(secret, Buffer.from("gate-response"));
  const responseBody = Buffer.alloc(encryptedResponse.length + 5);
  encryptedResponse.copy(responseBody);
  responseBody.writeUInt32BE(1, encryptedResponse.length);
  responseBody[responseBody.length - 1] = 0;
  socket.emit("message", framePackage(responseBody));
  const response = await responsePromise;
  assert.equal(response.gameSession, 1);
  assert.equal(response.legacyEncryptedFrame, false);
  assert.equal(response.messages[0].tag, 1);
});

test("game protocol accepts the MAS legacy whole-body DES frame", async () => {
  const socket = new FakeSocket();
  const secret = Buffer.from("78ab0d3f882c1787", "hex");
  const bridge = {
    async encodeGate() {
      return { packedBase64: Buffer.from("request").toString("base64") };
    },
    async decodeGate(packedBase64) {
      assert.equal(Buffer.from(packedBase64, "base64").toString(), "legacy-response");
      return { outerSession: 2, messages: [{ tag: 1, rpcType: "RESPONSE", payload: {} }] };
    },
  };
  const session = new CadGameProtocolSession({ socket, sharedSecret: secret, bridge });
  const responsePromise = session.send("Role_GetRoleList.request", {});
  await new Promise((resolve) => setImmediate(resolve));

  // 旧格式没有明文 game-session/flag 尾部，MAS 会把整个 body 作为 DES 密文解码。
  const encrypted = desEncode(secret, Buffer.from("legacy-response"));
  assert.notEqual(encrypted[encrypted.length - 1], 0);
  socket.emit("message", framePackage(encrypted));
  const response = await responsePromise;
  assert.equal(response.gameSession, null);
  assert.equal(response.legacyEncryptedFrame, true);
  assert.equal(response.messages[0].tag, 1);
});

test("game protocol rejects malformed DES payloads without consuming pending requests", async () => {
  const socket = new FakeSocket();
  const errors = [];
  const session = new CadGameProtocolSession({
    socket,
    sharedSecret: Buffer.from("78ab0d3f882c1787", "hex"),
    bridge: { async decodeGate() { throw new Error("must not decode"); } },
  });
  session.on("protocolError", (error) => errors.push(error));

  const malformedBody = Buffer.from([1, 2, 3, 4, 5, 6, 7]);
  socket.emit("message", framePackage(malformedBody));
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(errors.length, 1);
  assert.match(errors[0].message, /DES payload length/);
});

test("game protocol errors reject the matching request without waiting for timeout", async () => {
  const socket = new FakeSocket();
  const secret = Buffer.from("78ab0d3f882c1787", "hex");
  const bridge = {
    async encodeGate() {
      return { packedBase64: Buffer.from("request").toString("base64") };
    },
    async decodeGate() {
      return {
        outerSession: 2,
        messages: [{ rpcType: "ERROR", error: { errorCode: "1501", errorMessage: "bag full" } }],
      };
    },
  };
  const session = new CadGameProtocolSession({ socket, sharedSecret: secret, bridge });
  const responsePromise = session.send("Email_TakeEnclosure.request", {});
  await new Promise((resolve) => setImmediate(resolve));
  const encrypted = desEncode(secret, Buffer.from("response"));
  const body = Buffer.alloc(encrypted.length + 5);
  encrypted.copy(body);
  body.writeUInt32BE(1, encrypted.length);
  socket.emit("message", framePackage(body));
  await assert.rejects(responsePromise, (error) => error.code === "1501" && /bag full/.test(error.message));
});

test("server pushes are emitted when bundled with a matching response", async () => {
  const socket = new FakeSocket();
  const secret = Buffer.from("78ab0d3f882c1787", "hex");
  const bridge = {
    async encodeGate() {
      return { packedBase64: Buffer.from("request").toString("base64") };
    },
    async decodeGate() {
      return {
        outerSession: 2,
        messages: [
          { tag: 30000, rpcType: "REQUEST", payload: { roleInfo: { dailyAwarded: [1] } } },
          { tag: 1, rpcType: "RESPONSE", payload: {} },
        ],
      };
    },
  };
  const session = new CadGameProtocolSession({ socket, sharedSecret: secret, bridge });
  const pushes = [];
  session.on("push", (event) => pushes.push(event));
  const responsePromise = session.send("Role_GetRoleList.request", {});
  await new Promise((resolve) => setImmediate(resolve));
  const encrypted = desEncode(secret, Buffer.from("response"));
  const body = Buffer.alloc(encrypted.length + 5);
  encrypted.copy(body);
  body.writeUInt32BE(1, encrypted.length);
  socket.emit("message", framePackage(body));
  await responsePromise;

  assert.equal(pushes.length, 1);
  assert.deepEqual(pushes[0].messages.map((message) => message.tag), [30000]);
});
