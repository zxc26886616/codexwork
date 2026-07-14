const test = require("node:test");
const assert = require("node:assert/strict");
const { SprotoBridgeClient } = require("../protocol/sprotoBridgeClient");

test("协议桥使用客户端生成协议完成 GateMessage 双层编解码", async (t) => {
  const bridge = new SprotoBridgeClient({ timeoutMs: 15000 });
  t.after(() => bridge.stop());
  const encoded = await bridge.encodeGate(
    "Role_GetRoleList.request",
    { codeVersion: "test-version", gameNode: "game1" },
    1,
    2,
  );
  assert.equal(encoded.tag, 1);
  assert.equal(encoded.session, 1);
  assert.equal(encoded.gateSession, 2);

  const decoded = await bridge.decodeGate(encoded.packedBase64);
  assert.equal(decoded.messages.length, 1);
  assert.equal(decoded.messages[0].tag, 1);
  assert.equal(decoded.messages[0].protocolType, "SprotoType.Role_GetRoleList+request");
  assert.equal(decoded.messages[0].payload.codeVersion, "test-version");
  assert.equal(decoded.messages[0].payload.gameNode, "game1");
});
