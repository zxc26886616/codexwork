const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { EventEmitter } = require("node:events");
const { CadGameStateStore } = require("../automation/cadGameStateStore");

function createFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cad-state-"));
  const configDir = path.join(root, "Config", "Json");
  fs.mkdirSync(configDir, { recursive: true });
  fs.writeFileSync(
    path.join(configDir, "Task.json"),
    JSON.stringify({
      mDataMap: {
        "9007199254740993": {
          ID: "9007199254740993",
          type: 3,
          refreshType: 1,
          require: 2,
          activePoint: 60,
        },
        4000100: { ID: 4000100, type: 4, refreshType: 1, require: 1, activePoint: 99 },
      },
    }),
  );
  fs.writeFileSync(
    path.join(configDir, "Functions.json"),
    JSON.stringify({ mDataMap: { 102: { roleDayTimes: 1 }, 103: { roleDayTimes: 1 } } }),
  );
  fs.writeFileSync(
    path.join(configDir, "Config.json"),
    JSON.stringify({ mDataMap: { roleEnergyMax: { int32_Value: 3000 } } }),
  );
  return root;
}

test("daily state is push-driven, 64-bit safe, and calculates claimable boxes", () => {
  const protocol = new EventEmitter();
  const store = new CadGameStateStore({ protocol, unityProjectPath: createFixture() });

  assert.equal(store.getClaimableDailyTaskIds().ready, false);
  protocol.emit("push", {
    messages: [
      {
        tag: 30503,
        rpcType: "REQUEST",
        payload: {
          taskInfo: {
            large: { taskId: "9007199254740993", taskSchedule: 2, award: false },
          },
        },
      },
      {
        tag: 30000,
        rpcType: "REQUEST",
        payload: { roleInfo: { dailyAwarded: [1] } },
      },
    ],
  });

  assert.deepEqual(store.getClaimableDailyTaskIds(), {
    ready: true,
    ids: ["9007199254740993"],
  });
  store.markDailyTasksAwarded(["9007199254740993"]);
  assert.equal(store.getActivePoint(), 60);
  assert.deepEqual(store.getClaimableDailyBoxIndexes(), {
    ready: true,
    indexes: [],
    activePoint: 60,
  });
  store.updateRoleInfo({ dailyAwarded: [] });
  assert.deepEqual(store.getClaimableDailyBoxIndexes().indexes, [1]);
  store.stop();
});

test("timed energy requires role state and function quota, then deduplicates the window", () => {
  const protocol = new EventEmitter();
  const store = new CadGameStateStore({ protocol, unityProjectPath: createFixture() });
  assert.equal(store.getEnergyClaimEligibility(1, "2026-07-13:1").ready, false);

  protocol.emit("push", {
    messages: [
      { tag: 30000, rpcType: "REQUEST", payload: { roleInfo: { energy: 120 } } },
      { tag: 30003, rpcType: "REQUEST", payload: { functions: {} } },
    ],
  });
  assert.deepEqual(store.getEnergyClaimEligibility(1, "2026-07-13:1"), {
    ready: true,
    eligible: true,
    functionId: "102",
  });
  store.markEnergyClaimAttempted(1, "2026-07-13:1");
  assert.equal(store.getEnergyClaimEligibility(1, "2026-07-13:1").eligible, false);
  store.stop();
});
