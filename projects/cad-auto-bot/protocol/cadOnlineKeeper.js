class CadOnlineKeeper {
  constructor(options) {
    this.protocol = options.protocol;
    this.intervalMs = Math.max(1, Number(options.intervalSeconds || 15)) * 1000;
    this.timer = null;
    this.heartPending = false;
    this.onError = options.onError || (() => {});
    this.onUpdate = options.onUpdate || (() => {});
    this.onUnhealthy = options.onUnhealthy || (() => {});
    this.maxConsecutiveFailures = Math.max(1, Number(options.maxConsecutiveFailures || 3));
    this.consecutiveFailures = 0;
    this.lastHeartbeatAt = null;
    this.lastHeartbeatError = null;
    this.lastPongAt = null;
    this.unhealthySignaled = false;
    this.onPush = (event) => this.handlePush(event);
    this.onClosed = () => this.stop();
  }

  start() {
    if (this.timer) return;
    this.protocol.on("push", this.onPush);
    this.protocol.on("closed", this.onClosed);
    this.sendHeart();
    this.timer = setInterval(() => this.sendHeart(), this.intervalMs);
    this.timer.unref?.();
    this.notify();
  }

  snapshot() {
    return {
      running: Boolean(this.timer),
      heartPending: this.heartPending,
      consecutiveFailures: this.consecutiveFailures,
      maxConsecutiveFailures: this.maxConsecutiveFailures,
      lastHeartbeatAt: this.lastHeartbeatAt,
      lastHeartbeatError: this.lastHeartbeatError,
      lastPongAt: this.lastPongAt,
    };
  }

  notify() {
    this.onUpdate(this.snapshot());
  }

  async sendHeart() {
    if (this.heartPending) return;
    this.heartPending = true;
    this.notify();
    const nowMs = Date.now();
    try {
      await this.protocol.send("Role_Heart.request", {
        clientTime: String(nowMs * 10000),
        serverTime: String(nowMs),
      });
      this.consecutiveFailures = 0;
      this.lastHeartbeatAt = new Date().toISOString();
      this.lastHeartbeatError = null;
      this.unhealthySignaled = false;
    } catch (error) {
      this.consecutiveFailures += 1;
      this.lastHeartbeatError = error.message;
      this.onError(error);
      if (
        this.consecutiveFailures >= this.maxConsecutiveFailures &&
        !this.unhealthySignaled
      ) {
        this.unhealthySignaled = true;
        this.onUnhealthy(error, this.snapshot());
      }
    } finally {
      this.heartPending = false;
      this.notify();
    }
  }

  async handlePush(event) {
    for (const message of event.messages || []) {
      if (message.rpcType !== "REQUEST" || Number(message.tag) !== 30007) continue;
      try {
        await this.protocol.send("Role_Pong.request", {
          data: String(message.payload?.data || 0),
        });
        this.lastPongAt = new Date().toISOString();
        this.notify();
      } catch (error) {
        this.onError(error);
      }
    }
  }

  stop() {
    if (this.timer) clearInterval(this.timer);
    this.timer = null;
    this.protocol.off("push", this.onPush);
    this.protocol.off("closed", this.onClosed);
    this.notify();
  }
}

module.exports = {
  CadOnlineKeeper,
};
