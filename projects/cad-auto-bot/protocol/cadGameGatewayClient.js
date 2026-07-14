const { buildRedirectAuth, framePackage } = require("./cadGatewayCrypto");
const { buildLoginWebSocketUrl } = require("./cadLoginGatewayClient");

function toBuffer(data) {
  if (Buffer.isBuffer(data)) return data;
  if (data instanceof ArrayBuffer) return Buffer.from(data);
  if (ArrayBuffer.isView(data)) {
    return Buffer.from(data.buffer, data.byteOffset, data.byteLength);
  }
  return Buffer.from(String(data), "utf8");
}

function readFrame(data) {
  const packet = toBuffer(data);
  if (packet.length < 2) throw new Error("角色网关响应缺少二字节包头");
  const bodyLength = packet.readUInt16BE(0);
  if (packet.length < bodyLength + 2) throw new Error("角色网关响应包不完整");
  return packet.subarray(2, bodyLength + 2);
}

function connectGameGateway(options, dependencies = {}) {
  const WebSocketClass = dependencies.WebSocketClass || globalThis.WebSocket;
  if (!WebSocketClass) {
    return Promise.reject(new Error("当前 Node 运行时不支持 WebSocket"));
  }
  const connectIndex = Number(options.connectIndex || 1);
  const authToken = buildRedirectAuth(options.sharedSecret, {
    uid: options.auth.uid,
    serverName: options.auth.serverName,
    subId: options.auth.subId,
    connectIndex,
  });
  const url = buildLoginWebSocketUrl(
    {
      serverUrl: options.serverUrl,
      serverIp: options.auth.serverIp,
      port: options.auth.gatewayPort,
    },
    dependencies.now,
  );
  const timeoutMs = Math.max(1, Number(options.timeoutSeconds || 15)) * 1000;

  return new Promise((resolve, reject) => {
    let settled = false;
    const socket = dependencies.socketFactory
      ? dependencies.socketFactory(url)
      : new WebSocketClass(url);
    socket.binaryType = "arraybuffer";

    const fail = (error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      try {
        socket.close();
      } catch {
        // 测试传输或已断开的连接可以没有可用 close。
      }
      reject(error);
    };

    const timeout = setTimeout(() => fail(new Error("角色网关认证超时")), timeoutMs);

    socket.addEventListener("open", () => {
      socket.send(framePackage(Buffer.from(authToken, "utf8")));
    });

    socket.addEventListener("message", (event) => {
      if (settled) return;
      try {
        const response = readFrame(event.data).toString("utf8").replace(/\0/g, "").trim();
        const code = Number.parseInt(response.slice(0, 3), 10);
        if (code !== 200) {
          const error = new Error(`角色网关认证失败: ${Number.isInteger(code) ? code : "invalid"}`);
          error.code = code;
          fail(error);
          return;
        }
        settled = true;
        clearTimeout(timeout);
        resolve({
          socket,
          url,
          connectIndex,
          connectedAt: new Date().toISOString(),
        });
      } catch (error) {
        fail(error);
      }
    });

    socket.addEventListener("error", () => fail(new Error("角色网关 WebSocket 连接失败")));
    socket.addEventListener("close", () => {
      if (!settled) fail(new Error("角色网关在认证完成前关闭连接"));
    });
  });
}

module.exports = {
  connectGameGateway,
  readFrame,
};
