const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const { CadOnlineKeeper } = require("../protocol/cadOnlineKeeper");

test("online keeper sends heart and responds to server ping", async () => {
  const protocol = new EventEmitter();
  const calls = [];
  protocol.send = async (type, fields) => calls.push({ type, fields });
  const keeper = new CadOnlineKeeper({ protocol, intervalSeconds: 60 });
  keeper.start();
  await new Promise((resolve) => setImmediate(resolve));
  protocol.emit("push", {
    messages: [{ rpcType: "REQUEST", tag: 30007, payload: { data: "12345678901234567" } }],
  });
  await new Promise((resolve) => setImmediate(resolve));
  keeper.stop();

  assert.equal(calls[0].type, "Role_Heart.request");
  assert.match(calls[0].fields.clientTime, /^\d+$/);
  assert.deepEqual(calls[1], {
    type: "Role_Pong.request",
    fields: { data: "12345678901234567" },
  });
  assert.ok(keeper.snapshot().lastHeartbeatAt);
  assert.ok(keeper.snapshot().lastPongAt);
});

test("online keeper reports unhealthy after three consecutive heartbeat failures", async () => {
  const protocol = new EventEmitter();
  protocol.send = async () => {
    throw new Error("heartbeat timeout");
  };
  const errors = [];
  const unhealthy = [];
  const keeper = new CadOnlineKeeper({
    protocol,
    intervalSeconds: 60,
    maxConsecutiveFailures: 3,
    onError: (error) => errors.push(error.message),
    onUnhealthy: (error, state) => unhealthy.push({ error: error.message, state }),
  });
  keeper.start();
  await new Promise((resolve) => setImmediate(resolve));
  await keeper.sendHeart();
  await keeper.sendHeart();
  keeper.stop();

  assert.equal(errors.length, 3);
  assert.equal(unhealthy.length, 1);
  assert.equal(unhealthy[0].state.consecutiveFailures, 3);
  assert.equal(unhealthy[0].error, "heartbeat timeout");
});
