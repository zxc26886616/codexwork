const fs = require("node:fs");
const path = require("node:path");

const DAILY_TASK_PUSH_TAG = 30503;
const ROLE_INFO_PUSH_TAG = 30000;
const ROLE_FUNCTIONS_PUSH_TAG = 30003;
const DAILY_BOX_THRESHOLDS = [50, 100, 150];
const ENERGY_FUNCTION_IDS = { 1: "102", 2: "103" };

function asEntries(value) {
  if (!value || typeof value !== "object") return [];
  return Array.isArray(value) ? value.map((item, index) => [String(index), item]) : Object.entries(value);
}

class CadGameStateStore {
  constructor(options) {
    this.protocol = options.protocol;
    this.unityProjectPath = options.unityProjectPath;
    this.onError = options.onError || (() => {});
    this.dailyDefinitions = new Map();
    this.dailyTasks = new Map();
    this.roleFunctions = new Map();
    this.roleInfo = {};
    this.energyFunctionLimits = new Map();
    this.energyClaimAttempts = new Set();
    this.dailyTasksReceived = false;
    this.roleInfoReceived = false;
    this.roleFunctionsReceived = false;
    this.configError = null;
    this.energyConfigError = null;
    this.onPush = (event) => this.handlePush(event);
    this.loadDailyDefinitions();
    this.protocol.on("push", this.onPush);
  }

  loadDailyDefinitions() {
    try {
      const configPath = path.join(this.unityProjectPath, "Config", "Json", "Task.json");
      const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
      for (const [key, definition] of asEntries(config.mDataMap)) {
        if (Number(definition.type) !== 3 || Number(definition.refreshType) !== 1) continue;
        const id = String(definition.ID ?? key);
        this.dailyDefinitions.set(id, {
          id,
          require: Number(definition.require || 0),
          activePoint: Number(definition.activePoint || 0),
        });
      }
    } catch (error) {
      this.configError = `读取日常任务配置失败：${error.message}`;
      this.onError(new Error(this.configError));
    }

    try {
      const functionsPath = path.join(this.unityProjectPath, "Config", "Json", "Functions.json");
      const functionsConfig = JSON.parse(fs.readFileSync(functionsPath, "utf8"));
      for (const functionId of Object.values(ENERGY_FUNCTION_IDS)) {
        const definition = functionsConfig.mDataMap?.[functionId];
        const limit = Number(definition?.roleDayTimes);
        if (!Number.isInteger(limit) || limit < 1) {
          throw new Error(`Functions.json 缺少有效的 ${functionId}.roleDayTimes`);
        }
        this.energyFunctionLimits.set(functionId, limit);
      }

      const commonPath = path.join(this.unityProjectPath, "Config", "Json", "Config.json");
      const commonConfig = JSON.parse(fs.readFileSync(commonPath, "utf8"));
      this.roleEnergyMax = Number(commonConfig.mDataMap?.roleEnergyMax?.int32_Value);
      if (!Number.isFinite(this.roleEnergyMax) || this.roleEnergyMax < 1) {
        throw new Error("Config.json 缺少有效的 roleEnergyMax");
      }
    } catch (error) {
      this.energyConfigError = `读取定时体力配置失败：${error.message}`;
      this.onError(new Error(this.energyConfigError));
    }
  }

  handlePush(event) {
    for (const message of event.messages || []) {
      if (message.rpcType !== "REQUEST") continue;
      if (Number(message.tag) === DAILY_TASK_PUSH_TAG) {
        this.updateDailyTasks(message.payload?.taskInfo);
      } else if (Number(message.tag) === ROLE_INFO_PUSH_TAG) {
        this.updateRoleInfo(message.payload?.roleInfo);
      } else if (Number(message.tag) === ROLE_FUNCTIONS_PUSH_TAG) {
        this.updateRoleFunctions(message.payload?.functions);
      }
    }
  }

  updateDailyTasks(taskInfo) {
    this.dailyTasksReceived = true;
    for (const [key, task] of asEntries(taskInfo)) {
      if (!task) continue;
      const id = String(task.taskId ?? key);
      this.dailyTasks.set(id, {
        taskId: id,
        taskSchedule: Number(task.taskSchedule || 0),
        award: Boolean(task.award),
      });
    }
  }

  updateRoleInfo(roleInfo) {
    this.roleInfoReceived = true;
    this.roleInfo = { ...this.roleInfo, ...(roleInfo || {}) };
  }

  updateRoleFunctions(functions) {
    this.roleFunctionsReceived = true;
    for (const [key, value] of asEntries(functions)) {
      if (!value) continue;
      const functionId = String(value.functionId ?? key);
      this.roleFunctions.set(functionId, {
        functionId,
        useDayTimes: Number(value.useDayTimes || 0),
      });
    }
  }

  getEnergyClaimEligibility(timeType, attemptKey) {
    if (this.energyConfigError) {
      return { ready: false, eligible: false, reason: this.energyConfigError };
    }
    if (!this.roleInfoReceived) {
      return { ready: false, eligible: false, reason: "尚未收到服务器角色状态" };
    }
    if (!this.roleFunctionsReceived) {
      return { ready: false, eligible: false, reason: "尚未收到服务器角色功能次数" };
    }
    if (this.energyClaimAttempts.has(attemptKey)) {
      return { ready: true, eligible: false, reason: "当前角色本时段已经尝试领取" };
    }
    const energy = Number(this.roleInfo.energy);
    if (!Number.isFinite(energy)) {
      return { ready: false, eligible: false, reason: "服务器角色体力状态尚未就绪" };
    }
    if (energy >= this.roleEnergyMax) {
      return { ready: true, eligible: false, reason: "角色体力已达到上限" };
    }
    const functionId = ENERGY_FUNCTION_IDS[timeType];
    const limit = this.energyFunctionLimits.get(functionId);
    const used = this.roleFunctions.get(functionId)?.useDayTimes || 0;
    if (used >= limit) {
      return { ready: true, eligible: false, reason: "当前时段体力已经领取" };
    }
    return { ready: true, eligible: true, functionId };
  }

  markEnergyClaimAttempted(timeType, attemptKey) {
    this.energyClaimAttempts.add(attemptKey);
    const functionId = ENERGY_FUNCTION_IDS[timeType];
    const current = this.roleFunctions.get(functionId) || { functionId, useDayTimes: 0 };
    this.roleFunctions.set(functionId, { ...current, useDayTimes: current.useDayTimes + 1 });
  }

  getClaimableDailyTaskIds() {
    if (this.configError) return { ready: false, reason: this.configError, ids: [] };
    if (!this.dailyTasksReceived) {
      return { ready: false, reason: "尚未收到服务器日常任务状态", ids: [] };
    }
    const ids = [];
    for (const [id, task] of this.dailyTasks.entries()) {
      const definition = this.dailyDefinitions.get(id);
      if (definition && !task.award && task.taskSchedule >= definition.require) ids.push(id);
    }
    return { ready: true, ids };
  }

  markDailyTasksAwarded(ids) {
    for (const value of ids || []) {
      const id = String(value);
      const task = this.dailyTasks.get(id);
      if (task) task.award = true;
    }
  }

  getActivePoint() {
    let activePoint = 0;
    for (const [id, task] of this.dailyTasks.entries()) {
      if (task.award) activePoint += this.dailyDefinitions.get(id)?.activePoint || 0;
    }
    return activePoint;
  }

  getClaimableDailyBoxIndexes() {
    if (this.configError) return { ready: false, reason: this.configError, indexes: [] };
    if (!this.dailyTasksReceived) {
      return { ready: false, reason: "尚未收到服务器日常任务状态", indexes: [] };
    }
    if (!this.roleInfoReceived) {
      return { ready: false, reason: "尚未收到服务器角色状态", indexes: [] };
    }
    const awarded = new Set((this.roleInfo.dailyAwarded || []).map(Number));
    const activePoint = this.getActivePoint();
    const indexes = DAILY_BOX_THRESHOLDS
      .map((threshold, offset) => ({ threshold, index: offset + 1 }))
      .filter((item) => activePoint >= item.threshold && !awarded.has(item.index))
      .map((item) => item.index);
    return { ready: true, indexes, activePoint };
  }

  markDailyBoxAwarded(index) {
    const awarded = new Set((this.roleInfo.dailyAwarded || []).map(Number));
    awarded.add(Number(index));
    this.roleInfo.dailyAwarded = [...awarded];
  }

  snapshot() {
    return {
      dailyDefinitionCount: this.dailyDefinitions.size,
      dailyTaskCount: this.dailyTasks.size,
      dailyTasksReceived: this.dailyTasksReceived,
      roleInfoReceived: this.roleInfoReceived,
      roleFunctionsReceived: this.roleFunctionsReceived,
      activePoint: this.getActivePoint(),
      configError: this.configError,
      energyConfigError: this.energyConfigError,
    };
  }

  stop() {
    this.protocol.off?.("push", this.onPush);
    this.protocol.removeListener?.("push", this.onPush);
  }
}

module.exports = {
  CadGameStateStore,
  DAILY_BOX_THRESHOLDS,
};
