const test = require("node:test");
const assert = require("node:assert/strict");
const {
  promoteToPersistentSession,
  shouldExpireCredentialSession,
  stopGatewayResources,
} = require("../automation/cadSessionLifecycle");

test("未进入角色网关的草花凭证按短期 TTL 过期", () => {
  assert.equal(
    shouldExpireCredentialSession({ persistent: false, expiresAt: 999 }, 1000),
    true,
  );
  assert.equal(
    shouldExpireCredentialSession({ persistent: false, expiresAt: 1001 }, 1000),
    false,
  );
});

test("角色登录成功后转为持续会话并清除渠道密钥", () => {
  const session = {
    userId: "user-1",
    channelToken: "secret-token",
    sdkParam: "secret-sdk-param",
    expiresAt: 999,
  };
  promoteToPersistentSession(session, 1234);

  assert.equal(session.persistent, true);
  assert.equal(session.connectedAt, 1234);
  assert.equal(session.expiresAt, null);
  assert.equal("channelToken" in session, false);
  assert.equal("sdkParam" in session, false);
  assert.equal(shouldExpireCredentialSession(session, Number.MAX_SAFE_INTEGER), false);
});

test("断开会话会停止调度、心跳、状态监听、协议和网关", () => {
  const stopped = [];
  stopGatewayResources(
    {
      scheduler: { stop() { stopped.push("scheduler"); } },
      keeper: { stop() { stopped.push("keeper"); } },
      stateStore: { stop() { stopped.push("stateStore"); } },
      protocol: { close() { stopped.push("protocol"); } },
      game: { socket: { close() { stopped.push("socket"); } } },
      bridge: { async stop() { stopped.push("bridge"); } },
    },
    new Error("test disconnect"),
  );

  assert.deepEqual(stopped, [
    "scheduler",
    "keeper",
    "stateStore",
    "protocol",
    "socket",
    "bridge",
  ]);
});
