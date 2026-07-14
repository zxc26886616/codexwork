const test = require("node:test");
const assert = require("node:assert/strict");
const { CadTaskScheduler, createTaskDefinitions } = require("../automation/cadTaskScheduler");

function response(tag, payload) {
  return { messages: [{ tag, rpcType: "RESPONSE", payload }] };
}

test("automation tasks use client mail and safe daily-sign protocols", async () => {
  const calls = [];
  const protocol = {
    async send(type, fields) {
      calls.push({ type, fields });
      if (type === "Email_TakeEnclosure.request") {
        return response(451, { indexs: ["1", "2"], reward: [{}], items: [] });
      }
      return response(773, { rewards: [{}, {}] });
    },
  };
  const scheduler = new CadTaskScheduler({
    protocol,
    config: {
      tasks: { claimCharacterMail: true, claimAccountMail: true, dailySign: true },
    },
    taskDefinitions: createTaskDefinitions(protocol),
  });
  const result = await scheduler.runOnce("test");

  assert.equal(result.status, "completed");
  assert.deepEqual(calls[0], {
    type: "Email_TakeEnclosure.request",
    fields: { mailType: 1, indexs: [] },
  });
  assert.deepEqual(calls[1].fields, { mailType: 2, indexs: [] });
  assert.deepEqual(calls[2], {
    type: "Welfare_SignDay.request",
    fields: { isReSign: false, isCostItem: false, vipResign: false },
  });
  assert.equal(result.results[0].detail.claimedMailCount, 2);
});

test("one failed automation task does not stop later tasks", async () => {
  const calls = [];
  const scheduler = new CadTaskScheduler({
    protocol: {},
    config: { tasks: { first: true, second: true } },
    taskDefinitions: [
      { id: "first", label: "first", async run() { throw Object.assign(new Error("full"), { code: "1501" }); } },
      { id: "second", label: "second", async run() { calls.push("second"); return { ok: true }; } },
    ],
  });
  const result = await scheduler.runOnce("test");
  assert.equal(result.status, "partial");
  assert.equal(result.results[0].errorCode, "1501");
  assert.deepEqual(calls, ["second"]);
});

test("daily tasks are claimed before eligible activity boxes", async () => {
  const calls = [];
  let tasksAwarded = false;
  const protocol = {
    async send(type, fields) {
      calls.push({ type, fields });
      if (type === "Task_TaskFinish.request") {
        return response(501, { taskIds: ["3000200"], reward: [{}] });
      }
      return response(502, { rewards: [{}, {}] });
    },
  };
  const stateStore = {
    getClaimableDailyTaskIds() {
      return { ready: true, ids: ["3000200"] };
    },
    markDailyTasksAwarded() {
      tasksAwarded = true;
    },
    getActivePoint() {
      return tasksAwarded ? 100 : 0;
    },
    getClaimableDailyBoxIndexes() {
      assert.equal(tasksAwarded, true);
      return { ready: true, indexes: [1, 2], activePoint: 100 };
    },
    markDailyBoxAwarded() {},
  };
  const scheduler = new CadTaskScheduler({
    protocol,
    stateStore,
    config: { tasks: { claimDailyTasks: true, claimDailyBoxes: true } },
  });
  const result = await scheduler.runOnce("test");

  assert.equal(result.status, "completed");
  assert.deepEqual(calls, [
    {
      type: "Task_TaskFinish.request",
      fields: { taskType: 3, taskIds: ["3000200"] },
    },
    { type: "Task_AwardDailyBox.request", fields: { index: 1 } },
    { type: "Task_AwardDailyBox.request", fields: { index: 2 } },
  ]);
});

test("daily automation skips safely before server state arrives", async () => {
  const scheduler = new CadTaskScheduler({
    protocol: { async send() { throw new Error("must not send"); } },
    stateStore: {
      getClaimableDailyTaskIds() {
        return { ready: false, reason: "not ready", ids: [] };
      },
    },
    config: { tasks: { claimDailyTasks: true } },
  });
  const result = await scheduler.runOnce("test");
  assert.equal(result.results[0].status, "skipped");
  assert.equal(result.results[0].detail.reason, "not ready");
});

test("configured battle sweep is bounded and uses the client protocol", async () => {
  const calls = [];
  const scheduler = new CadTaskScheduler({
    protocol: {
      async send(type, fields) {
        calls.push({ type, fields });
        return response(228, { reward: [{}, {}] });
      },
    },
    config: {
      tasks: { wipeBattle: true },
      wipeBattle: { battleId: "9007199254740993", times: 3 },
    },
  });
  const result = await scheduler.runOnce("test");

  assert.equal(result.results[0].status, "completed");
  assert.deepEqual(calls, [
    {
      type: "Battle_WipeOut.request",
      fields: { battleId: "9007199254740993", times: 3 },
    },
  ]);
  assert.equal(result.results[0].detail.rewardEntries, 2);
});

test("battle sweep skips invalid or excessive requests", async () => {
  const scheduler = new CadTaskScheduler({
    protocol: { async send() { throw new Error("must not send"); } },
    config: {
      tasks: { wipeBattle: true },
      wipeBattle: { battleId: "123", times: 11 },
    },
  });
  const result = await scheduler.runOnce("test");
  assert.equal(result.results[0].status, "skipped");
  assert.match(result.results[0].detail.reason, /1–10/);
});

test("timed energy only uses the free claim protocol inside the Shanghai time window", async () => {
  const calls = [];
  const attempts = [];
  const scheduler = new CadTaskScheduler({
    protocol: {
      async send(type, fields) {
        calls.push({ type, fields });
        return response(771, { timeType: 1 });
      },
    },
    stateStore: {
      getEnergyClaimEligibility() {
        return { ready: true, eligible: true };
      },
      markEnergyClaimAttempted(timeType, key) {
        attempts.push({ timeType, key });
      },
    },
    now: () => new Date("2026-07-13T04:30:00.000Z"),
    config: { tasks: { claimTimedEnergy: true } },
  });

  const result = await scheduler.runOnce("test");
  assert.equal(result.results[0].status, "completed");
  assert.deepEqual(calls, [
    { type: "Welfare_PickEnergy.request", fields: { timeType: 1 } },
  ]);
  assert.deepEqual(attempts, [{ timeType: 1, key: "2026-07-13:1" }]);
  assert.equal(calls.some((call) => call.type === "Welfare_RePickEnergy.request"), false);
});

test("timed energy skips outside its windows and when state rejects a duplicate", async () => {
  let sendCount = 0;
  const protocol = { async send() { sendCount += 1; } };
  const outside = new CadTaskScheduler({
    protocol,
    stateStore: {},
    now: () => new Date("2026-07-13T07:00:00.000Z"),
    config: { tasks: { claimTimedEnergy: true } },
  });
  const outsideResult = await outside.runOnce("test");
  assert.equal(outsideResult.results[0].status, "skipped");

  const duplicate = new CadTaskScheduler({
    protocol,
    stateStore: {
      getEnergyClaimEligibility() {
        return { ready: true, eligible: false, reason: "当前角色本时段已经尝试领取" };
      },
    },
    now: () => new Date("2026-07-13T11:30:00.000Z"),
    config: { tasks: { claimTimedEnergy: true } },
  });
  const duplicateResult = await duplicate.runOnce("test");
  assert.equal(duplicateResult.results[0].status, "skipped");
  assert.equal(sendCount, 0);
});
