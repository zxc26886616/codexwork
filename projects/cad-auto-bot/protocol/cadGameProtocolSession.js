const { EventEmitter } = require("node:events");
const { desDecode, desEncode, framePackage } = require("./cadGatewayCrypto");

function toBuffer(data) {
  if (Buffer.isBuffer(data)) return data;
  if (data instanceof ArrayBuffer) return Buffer.from(data);
  if (ArrayBuffer.isView(data)) {
    return Buffer.from(data.buffer, data.byteOffset, data.byteLength);
  }
  return Buffer.from(data);
}

class CadGameProtocolSession extends EventEmitter {
  constructor(options) {
    super();
    this.socket = options.socket;
    this.sharedSecret = Buffer.from(options.sharedSecret);
    this.bridge = options.bridge;
    this.timeoutMs = Math.max(1, Number(options.timeoutSeconds || 15)) * 1000;
    this.sprotoSession = 0;
    this.gameSession = 0;
    this.pending = new Map();
    this.onMessage = (event) => this.handleMessage(event.data);
    this.onClose = () => {
      this.close(new Error("Game gateway connection closed"));
      this.emit("closed");
    };
    this.socket.addEventListener("message", this.onMessage);
    this.socket.addEventListener("close", this.onClose);
  }

  async send(type, fields = {}) {
    const businessSession = this.sprotoSession + 1;
    const gateSession = this.sprotoSession + 2;
    this.sprotoSession += 3;
    this.gameSession += 1;
    const encoded = await this.bridge.encodeGate(
      type,
      fields,
      businessSession,
      gateSession,
    );
    const encrypted = desEncode(
      this.sharedSecret,
      Buffer.from(encoded.packedBase64, "base64"),
    );
    const body = Buffer.alloc(encrypted.length + 4);
    encrypted.copy(body);
    body.writeUInt32BE(this.gameSession, encrypted.length);

    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(gateSession);
        reject(new Error(`Game protocol request timed out: ${type}`));
      }, this.timeoutMs);
      this.pending.set(gateSession, { type, resolve, reject, timeout });
      try {
        this.socket.send(framePackage(body));
      } catch (error) {
        clearTimeout(timeout);
        this.pending.delete(gateSession);
        reject(error);
      }
    });
  }

  async handleMessage(data) {
    try {
      const packet = toBuffer(data);
      if (packet.length < 7) throw new Error("Game protocol frame is incomplete");
      const bodyLength = packet.readUInt16BE(0);
      if (packet.length < bodyLength + 2) throw new Error("Game protocol frame is incomplete");
      const body = packet.subarray(2, bodyLength + 2);
      if (body.length < 5) throw new Error("Game protocol response metadata is missing");
      const legacyEncryptedFrame = body[body.length - 1] !== 0;
      const gameSession = legacyEncryptedFrame
        ? null
        : body.readUInt32BE(body.length - 5);
      // MAS 的 is_compressed 分支没有执行解压，而是把整个 body 交给 DES。
      // 该分支更像旧服帧格式兼容；请求匹配仍以解密后的 outerSession 为准。
      const encrypted = legacyEncryptedFrame ? body : body.subarray(0, body.length - 5);
      if (encrypted.length === 0 || encrypted.length % 8 !== 0) {
        throw new Error("Game protocol DES payload length is invalid");
      }
      const plain = desDecode(this.sharedSecret, encrypted);
      const decoded = await this.bridge.decodeGate(plain.toString("base64"));
      const pushMessages = (decoded.messages || []).filter(
        (message) => message.rpcType === "REQUEST",
      );
      if (pushMessages.length > 0) {
        // 服务端可能把角色/任务推送与当前请求回包放在同一个 Gate 包中。
        this.emit("push", {
          gameSession,
          legacyEncryptedFrame,
          ...decoded,
          messages: pushMessages,
        });
      }
      const pending = this.pending.get(Number(decoded.outerSession));
      if (pending) {
        clearTimeout(pending.timeout);
        this.pending.delete(Number(decoded.outerSession));
        const protocolError = (decoded.messages || []).find((message) => message.error)?.error;
        if (protocolError) {
          const error = new Error(
            protocolError.errorMessage ||
              `Game protocol error ${protocolError.errorCode || "unknown"}: ${pending.type}`,
          );
          error.code = protocolError.errorCode;
          pending.reject(error);
          return;
        }
        pending.resolve({ gameSession, legacyEncryptedFrame, ...decoded });
      } else if (pushMessages.length === 0) {
        this.emit("push", { gameSession, legacyEncryptedFrame, ...decoded });
      }
    } catch (error) {
      this.emit("protocolError", error);
    }
  }

  close(error = new Error("Game protocol session closed")) {
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.pending.clear();
  }
}

module.exports = {
  CadGameProtocolSession,
};
