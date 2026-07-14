const test = require("node:test");
const assert = require("node:assert/strict");
const {
  assertExistingSessionCanConnect,
  connectExistingSession,
} = require("../automation/cadExistingSessionConnector");

function createState() {
  return {
    config: { gateway: { enabled: true, serverNode: "game908" } },
    session: {
      sessionId: "session-1",
      userId: "user-1",
      channelToken: "token-1",
      sdkParam: "sdk-param-1",
    },
    publicSession: { sessionId: "session-1", userId: "***" },
  };
}

test("已有草花会话必须与当前 Web 会话一致", () => {
  const state = createState();
  state.publicSession.sessionId = "stale-session";
  assert.throws(
    () => assertExistingSessionCanConnect(state),
    /会话状态不一致/,
  );
});

test("未启用网关或已有网关时拒绝复用凭证", () => {
  const disabled = createState();
  disabled.config.gateway.enabled = false;
  assert.throws(
    () => assertExistingSessionCanConnect(disabled),
    /请先启用角色网关/,
  );

  const connected = createState();
  connected.session.gateway = { connected: true };
  assert.throws(
    () => assertExistingSessionCanConnect(connected),
    /已经连接角色网关/,
  );
});

test("连接所选区服复用内存凭证且不要求再次提交账号密码", async () => {
  const state = createState();
  let received = null;
  const gateway = await connectExistingSession({
    ...state,
    connectLoginGateway: async (config, session, publicSession) => {
      received = { config, session, publicSession };
      return {
        connected: true,
        roleAuthenticated: true,
        role: { roleId: "9223372036854775807" },
      };
    },
  });

  assert.equal(received.session.channelToken, "token-1");
  assert.equal(received.session.sdkParam, "sdk-param-1");
  assert.equal(received.publicSession.sessionId, "session-1");
  assert.equal(received.config.gateway.serverNode, "game908");
  assert.equal(gateway.roleAuthenticated, true);
});

test("网关连接器必须返回明确的成功结果", async () => {
  const state = createState();
  await assert.rejects(
    connectExistingSession({
      ...state,
      connectLoginGateway: async () => null,
    }),
    /未返回有效连接结果/,
  );
  assert.equal(state.session.connecting, undefined);
});

test("同一草花会话不能并发连接两次", async () => {
  const state = createState();
  let release;
  const first = connectExistingSession({
    ...state,
    connectLoginGateway: () => new Promise((resolve) => {
      release = () => resolve({ connected: true });
    }),
  });

  await assert.rejects(
    connectExistingSession({
      ...state,
      connectLoginGateway: async () => ({ connected: true }),
    }),
    /正在连接所选区服/,
  );
  release();
  await first;
  assert.equal(state.session.connecting, undefined);
});
