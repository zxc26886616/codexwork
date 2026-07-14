const { reconnectDelayMs } = require("./cadSecretVault");

class CadReconnectLoop {
  constructor(options) {
    this.getConfig = options.getConfig;
    this.connect = options.connect;
    this.onUpdate = options.onUpdate || (() => {});
    this.onAttemptFailure = options.onAttemptFailure || (() => {});
    this.onSuccess = options.onSuccess || (() => {});
    this.onExhausted = options.onExhausted || (() => {});
    this.setTimer = options.setTimer || setTimeout;
    this.clearTimer = options.clearTimer || clearTimeout;
    this.active = false;
    this.timer = null;
    this.attempt = 0;
    this.lastError = null;
  }

  start() {
    if (this.active) return this.snapshot();
    this.active = true;
    this.schedule(1);
    return this.snapshot();
  }

  schedule(attempt) {
    if (!this.active) return;
    const config = this.getConfig();
    const maxAttempts = Math.max(1, Number(config.reconnectAttempts || 5));
    if (!config.autoReconnect || attempt > maxAttempts) {
      this.active = false;
      this.timer = null;
      this.onExhausted(this.snapshot());
      return;
    }
    this.attempt = attempt;
    const delayMs = reconnectDelayMs(attempt, config.reconnectIntervalSeconds);
    this.onUpdate({
      ...this.snapshot(),
      maxAttempts,
      nextAttemptAt: new Date(Date.now() + delayMs).toISOString(),
    });
    this.timer = this.setTimer(() => this.runAttempt(attempt), delayMs);
    this.timer?.unref?.();
  }

  async runAttempt(attempt) {
    if (!this.active || attempt !== this.attempt) return;
    this.timer = null;
    try {
      await this.connect(attempt);
      if (!this.active) return;
      this.active = false;
      this.lastError = null;
      this.onSuccess(this.snapshot());
    } catch (error) {
      if (!this.active) return;
      this.lastError = error.message;
      this.onAttemptFailure(error, this.snapshot());
      this.schedule(attempt + 1);
    }
  }

  stop() {
    this.active = false;
    this.clearTimer(this.timer);
    this.timer = null;
  }

  snapshot() {
    return {
      active: this.active,
      attempt: this.attempt,
      lastError: this.lastError,
    };
  }
}

module.exports = {
  CadReconnectLoop,
};
