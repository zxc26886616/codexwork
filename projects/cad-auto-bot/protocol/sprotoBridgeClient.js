const crypto = require("node:crypto");
const fs = require("node:fs");
const path = require("node:path");
const { spawn } = require("node:child_process");
const readline = require("node:readline");

class SprotoBridgeClient {
  constructor(options = {}) {
    this.rootDir = options.rootDir || path.resolve(__dirname, "..");
    this.timeoutMs = options.timeoutMs || 10000;
    this.process = null;
    this.pending = new Map();
  }

  get dllPath() {
    return path.join(
      this.rootDir,
      "protocol-bridge",
      "bin",
      "Release",
      "net10.0",
      "CadSprotoBridge.dll",
    );
  }

  async start() {
    if (this.process) return;
    if (!fs.existsSync(this.dllPath)) {
      throw new Error("Sproto 协议桥尚未编译，请先执行 npm run build:bridge");
    }
    this.process = spawn("dotnet", [this.dllPath], {
      cwd: this.rootDir,
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    });
    const lines = readline.createInterface({ input: this.process.stdout });
    lines.on("line", (line) => this.handleLine(line));
    this.process.once("exit", (code) => {
      const error = new Error(`Sproto 协议桥已退出: ${code}`);
      for (const item of this.pending.values()) item.reject(error);
      this.pending.clear();
      this.process = null;
    });
    await this.request({ op: "ping" });
  }

  handleLine(line) {
    let response;
    try {
      response = JSON.parse(line);
    } catch {
      return;
    }
    const item = this.pending.get(response.id);
    if (!item) return;
    clearTimeout(item.timeout);
    this.pending.delete(response.id);
    if (response.ok) item.resolve(response.data);
    else item.reject(new Error(response.error || "Sproto 协议桥调用失败"));
  }

  async request(command) {
    if (!this.process && command.op !== "ping") await this.start();
    const id = crypto.randomUUID();
    const payload = { ...command, id };
    return new Promise((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.pending.delete(id);
        reject(new Error("Sproto 协议桥调用超时"));
      }, this.timeoutMs);
      this.pending.set(id, { resolve, reject, timeout });
      this.process.stdin.write(`${JSON.stringify(payload)}\n`);
    });
  }

  encodeGate(type, fields, session, gateSession) {
    return this.request({ op: "encodeGate", type, fields, session, gateSession });
  }

  decodeGate(packedBase64) {
    return this.request({ op: "decodeGate", packedBase64 });
  }

  async stop() {
    const child = this.process;
    if (!child) return;
    this.process = null;
    child.stdin.end();
    await new Promise((resolve) => {
      child.once("exit", resolve);
      setTimeout(() => {
        if (!child.killed) child.kill();
        resolve();
      }, 1000).unref();
    });
  }
}

module.exports = {
  SprotoBridgeClient,
};
