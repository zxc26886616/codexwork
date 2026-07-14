const test = require("node:test");
const assert = require("node:assert/strict");
const { CadReconnectLoop } = require("../automation/cadReconnectLoop");

function createFakeTimers() {
  const jobs = [];
  return {
    jobs,
    setTimer(callback, delay) {
      const timer = { callback, delay, unref() {} };
      jobs.push(timer);
      return timer;
    },
    clearTimer(timer) {
      const index = jobs.indexOf(timer);
      if (index >= 0) jobs.splice(index, 1);
    },
  };
}

test("自动重连失败后继续，成功后停止并保留尝试次数", async () => {
  const timers = createFakeTimers();
  const attempts = [];
  const updates = [];
  const success = [];
  const loop = new CadReconnectLoop({
    getConfig: () => ({
      autoReconnect: true,
      reconnectAttempts: 3,
      reconnectIntervalSeconds: 2,
    }),
    async connect(attempt) {
      attempts.push(attempt);
      if (attempt === 1) throw new Error("temporary network error");
    },
    onUpdate: (state) => updates.push(state),
    onSuccess: (state) => success.push(state),
    setTimer: timers.setTimer,
    clearTimer: timers.clearTimer,
  });

  loop.start();
  assert.equal(timers.jobs[0].delay, 2000);
  await timers.jobs.shift().callback();
  assert.equal(timers.jobs[0].delay, 4000);
  await timers.jobs.shift().callback();

  assert.deepEqual(attempts, [1, 2]);
  assert.equal(loop.active, false);
  assert.equal(success.length, 1);
  assert.equal(success[0].attempt, 2);
  assert.deepEqual(updates.map((state) => state.attempt), [1, 2]);
});

test("自动重连达到上限后结束，主动停止会取消待执行任务", async () => {
  const timers = createFakeTimers();
  const exhausted = [];
  const loop = new CadReconnectLoop({
    getConfig: () => ({
      autoReconnect: true,
      reconnectAttempts: 2,
      reconnectIntervalSeconds: 1,
    }),
    async connect() {
      throw new Error("offline");
    },
    onExhausted: (state) => exhausted.push(state),
    setTimer: timers.setTimer,
    clearTimer: timers.clearTimer,
  });

  loop.start();
  await timers.jobs.shift().callback();
  await timers.jobs.shift().callback();
  assert.equal(exhausted.length, 1);
  assert.equal(loop.active, false);

  loop.start();
  assert.equal(timers.jobs.length, 1);
  loop.stop();
  assert.equal(timers.jobs.length, 0);
  assert.equal(loop.active, false);
});
