const crypto = require("node:crypto");

class CadSecretVault {
  constructor(key = crypto.randomBytes(32)) {
    this.key = Buffer.from(key);
    if (this.key.length !== 32) throw new Error("密钥仓库需要 32 字节密钥");
  }

  seal(value) {
    const iv = crypto.randomBytes(12);
    const cipher = crypto.createCipheriv("aes-256-gcm", this.key, iv);
    const encrypted = Buffer.concat([
      cipher.update(JSON.stringify(value), "utf8"),
      cipher.final(),
    ]);
    return {
      iv: iv.toString("base64"),
      tag: cipher.getAuthTag().toString("base64"),
      data: encrypted.toString("base64"),
    };
  }

  open(sealed) {
    const decipher = crypto.createDecipheriv(
      "aes-256-gcm",
      this.key,
      Buffer.from(sealed.iv, "base64"),
    );
    decipher.setAuthTag(Buffer.from(sealed.tag, "base64"));
    const plain = Buffer.concat([
      decipher.update(Buffer.from(sealed.data, "base64")),
      decipher.final(),
    ]);
    return JSON.parse(plain.toString("utf8"));
  }

  clear() {
    this.key.fill(0);
  }
}

function reconnectDelayMs(attempt, intervalSeconds, maxSeconds = 60) {
  const base = Math.max(1, Number(intervalSeconds || 5));
  const seconds = Math.min(Math.max(base, 1) * 2 ** Math.max(0, attempt - 1), maxSeconds);
  return seconds * 1000;
}

module.exports = {
  CadSecretVault,
  reconnectDelayMs,
};
