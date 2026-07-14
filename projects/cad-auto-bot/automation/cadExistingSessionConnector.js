function assertExistingSessionCanConnect({ config, session, publicSession }) {
  if (!session || !publicSession) {
    throw new Error("草花渠道会话不存在或已经过期");
  }
  if (!session.sessionId || session.sessionId !== publicSession.sessionId) {
    throw new Error("草花渠道会话状态不一致，请重新登录");
  }
  if (!config?.gateway?.enabled) {
    throw new Error("请先启用角色网关并保存所选区服参数");
  }
  if (session.gateway) {
    throw new Error("当前草花会话已经连接角色网关；如需切服，请先主动断开后重新登录");
  }
  if (session.connecting) {
    throw new Error("当前草花会话正在连接所选区服，请勿重复提交");
  }
}

async function connectExistingSession({
  config,
  session,
  publicSession,
  connectLoginGateway,
}) {
  assertExistingSessionCanConnect({ config, session, publicSession });
  if (typeof connectLoginGateway !== "function") {
    throw new Error("角色网关连接器不可用");
  }
  session.connecting = true;
  try {
    const gateway = await connectLoginGateway(config, session, publicSession);
    if (!gateway?.connected) {
      throw new Error("角色网关未返回有效连接结果");
    }
    return gateway;
  } finally {
    delete session.connecting;
  }
}

module.exports = {
  assertExistingSessionCanConnect,
  connectExistingSession,
};
