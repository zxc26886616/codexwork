function shouldExpireCredentialSession(session, now = Date.now()) {
  return !session.persistent && Number(session.expiresAt || 0) <= now;
}

function promoteToPersistentSession(session, connectedAt = Date.now()) {
  session.persistent = true;
  session.connectedAt = connectedAt;
  session.expiresAt = null;
  // 角色网关认证完成后，持续在线不再需要保留渠道密钥。
  delete session.channelToken;
  delete session.sdkParam;
  return session;
}

function stopGatewayResources(gateway, error = new Error("角色网关会话已断开")) {
  if (!gateway) return;
  gateway.scheduler?.stop();
  gateway.keeper?.stop();
  gateway.stateStore?.stop();
  gateway.protocol?.close(error);
  try {
    gateway.game?.socket?.close();
  } catch {
    // 关闭中的 WebSocket 可能已经不可用，其余资源仍需继续清理。
  }
  gateway.bridge?.stop().catch(() => {});
}

module.exports = {
  promoteToPersistentSession,
  shouldExpireCredentialSession,
  stopGatewayResources,
};
