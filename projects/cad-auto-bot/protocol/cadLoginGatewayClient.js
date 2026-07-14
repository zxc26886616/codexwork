const {
  createRandomKey,
  dhExchange,
  dhSecret,
  encryptLoginToken,
  hmac64,
  parseAuthResponse,
} = require("./cadGatewayCrypto");

function toBuffer(data) {
  if (Buffer.isBuffer(data)) return data;
  if (data instanceof ArrayBuffer) return Buffer.from(data);
  if (ArrayBuffer.isView(data)) {
    return Buffer.from(data.buffer, data.byteOffset, data.byteLength);
  }
  return Buffer.from(String(data), "utf8");
}

function decodeEightByteLine(data, name) {
  const encoded = toBuffer(data).toString("utf8").replace(/\0/g, "").trim();
  const decoded = Buffer.from(encoded, "base64");
  if (decoded.length !== 8) {
    throw new Error(`${name} must decode to exactly 8 bytes`);
  }
  return decoded;
}

function sendBase64Line(socket, value) {
  socket.send(Buffer.from(`${Buffer.from(value).toString("base64")}\n`, "utf8"));
}

function buildLoginWebSocketUrl(gateway, now = Date.now) {
  const rawUrl = String(gateway.serverUrl || "").trim();
  if (!/^wss?:\/\//i.test(rawUrl)) {
    throw new Error("登录服 serverUrl 必须以 ws:// 或 wss:// 开头");
  }
  const url = new URL(rawUrl);
  if (gateway.serverIp) url.searchParams.set("host", gateway.serverIp);
  if (gateway.port) url.searchParams.set("port", String(gateway.port));
  url.searchParams.set("rand", String(now()));
  return url.toString();
}

function authenticateLoginGateway(options, dependencies = {}) {
  const WebSocketClass = dependencies.WebSocketClass || globalThis.WebSocket;
  if (!WebSocketClass) {
    return Promise.reject(new Error("当前 Node 运行时不支持 WebSocket"));
  }

  const timeoutMs = Math.max(1, Number(options.timeoutSeconds || 15)) * 1000;
  const clientKey = options.clientKey
    ? Buffer.from(options.clientKey)
    : createRandomKey(dependencies.randomBytes);

  return new Promise((resolve, reject) => {
    let settled = false;
    let state = "challenge";
    let challenge;
    let sharedSecret;
    const socket = dependencies.socketFactory
      ? dependencies.socketFactory(options.url)
      : new WebSocketClass(options.url);
    socket.binaryType = "arraybuffer";

    const finish = (error, result) => {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      try {
        socket.close();
      } catch {
        // 测试传输或已断开的连接可以没有可用 close。
      }
      if (error) reject(error);
      else resolve(result);
    };

    const timeout = setTimeout(() => {
      finish(new Error("登录服握手超时"));
    }, timeoutMs);

    socket.addEventListener("message", (event) => {
      try {
        if (state === "challenge") {
          challenge = decodeEightByteLine(event.data, "gateway challenge");
          sendBase64Line(socket, dhExchange(clientKey));
          state = "serverKey";
          return;
        }

        if (state === "serverKey") {
          const serverKey = decodeEightByteLine(event.data, "gateway DH key");
          sharedSecret = dhSecret(serverKey, clientKey);
          sendBase64Line(socket, hmac64(challenge, sharedSecret));
          state = "authResult";
          const tokenDelayMs = dependencies.tokenDelayMs ?? 50;
          setTimeout(() => {
            if (!settled) {
              sendBase64Line(socket, encryptLoginToken(sharedSecret, options));
            }
          }, tokenDelayMs);
          return;
        }

        if (state === "authResult") {
          const auth = parseAuthResponse(toBuffer(event.data).toString("utf8"));
          finish(null, {
            auth,
            sharedSecret,
          });
        }
      } catch (error) {
        finish(error);
      }
    });

    socket.addEventListener("error", () => {
      finish(new Error("登录服 WebSocket 连接失败"));
    });

    socket.addEventListener("close", () => {
      if (!settled) finish(new Error("登录服在握手完成前关闭连接"));
    });
  });
}

module.exports = {
  authenticateLoginGateway,
  buildLoginWebSocketUrl,
  decodeEightByteLine,
};
