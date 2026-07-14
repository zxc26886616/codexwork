const { findResponse } = require("../protocol/cadRoleLoginFlow");

function summarizeMail(payload) {
  return {
    claimedMailCount: Array.isArray(payload.indexs) ? payload.indexs.length : 0,
    rewardEntries: Array.isArray(payload.reward) ? payload.reward.length : 0,
    itemEntries: Array.isArray(payload.items) ? payload.items.length : 0,
  };
}

function getShanghaiEnergyWindow(now) {
  const parts = Object.fromEntries(
    new Intl.DateTimeFormat("en-CA", {
      timeZone: "Asia/Shanghai",
      year: "numeric",
      month: "2-digit",
      day: "2-digit",
      hour: "2-digit",
      hourCycle: "h23",
    }).formatToParts(now).filter((part) => part.type !== "literal").map((part) => [part.type, part.value]),
  );
  const hour = Number(parts.hour);
  const timeType = hour >= 12 && hour < 14 ? 1 : hour >= 19 && hour < 21 ? 2 : 0;
  if (!timeType) return null;
  return {
    timeType,
    attemptKey: `${parts.year}-${parts.month}-${parts.day}:${timeType}`,
  };
}

function createTaskDefinitions(protocol, stateStore, options = {}) {
  const now = options.now || (() => new Date());
  return [
    {
      id: "claimCharacterMail",
      label: "领取角色邮件附件",
      async run() {
        const response = await protocol.send("Email_TakeEnclosure.request", {
          mailType: 1,
          indexs: [],
        });
        return summarizeMail(findResponse(response, 451));
      },
    },
    {
      id: "claimAccountMail",
      label: "领取账号邮件附件",
      async run() {
        const response = await protocol.send("Email_TakeEnclosure.request", {
          mailType: 2,
          indexs: [],
        });
        return summarizeMail(findResponse(response, 451));
      },
    },
    {
      id: "dailySign",
      label: "每日普通签到",
      async run() {
        const response = await protocol.send("Welfare_SignDay.request", {
          isReSign: false,
          isCostItem: false,
          vipResign: false,
        });
        const payload = findResponse(response, 773);
        return {
          rewardEntries: Array.isArray(payload.rewards) ? payload.rewards.length : 0,
        };
      },
    },
    {
      id: "claimTimedEnergy",
      label: "定时领取午间/晚间体力",
      async run() {
        const window = getShanghaiEnergyWindow(now());
        if (!window) {
          return { skipped: true, reason: "当前不在午间或晚间免费领取时段" };
        }
        const eligibility = stateStore?.getEnergyClaimEligibility?.(
          window.timeType,
          window.attemptKey,
        );
        if (!eligibility?.ready || !eligibility.eligible) {
          return {
            skipped: true,
            reason: eligibility?.reason || "定时体力状态缓存未启用",
          };
        }
        // 先登记尝试，避免调度器在同一时段因超时或错误重复发送领取请求。
        stateStore.markEnergyClaimAttempted(window.timeType, window.attemptKey);
        const response = await protocol.send("Welfare_PickEnergy.request", {
          timeType: window.timeType,
        });
        const payload = findResponse(response, 771);
        return { timeType: Number(payload.timeType || window.timeType) };
      },
    },
    {
      id: "claimDailyTasks",
      label: "领取已完成日常任务",
      async run() {
        const claimable = stateStore?.getClaimableDailyTaskIds();
        if (!claimable?.ready) {
          return { skipped: true, reason: claimable?.reason || "日常任务状态缓存未启用" };
        }
        if (claimable.ids.length === 0) {
          return { skipped: true, reason: "当前没有可领取的日常任务" };
        }
        const response = await protocol.send("Task_TaskFinish.request", {
          taskType: 3,
          taskIds: claimable.ids,
        });
        const payload = findResponse(response, 501);
        const awardedIds = payload.taskIds || claimable.ids;
        stateStore.markDailyTasksAwarded(awardedIds);
        return {
          claimedTaskCount: awardedIds.length,
          rewardEntries: Array.isArray(payload.reward) ? payload.reward.length : 0,
          activePoint: stateStore.getActivePoint(),
        };
      },
    },
    {
      id: "claimDailyBoxes",
      label: "领取日常活跃宝箱",
      async run() {
        const claimable = stateStore?.getClaimableDailyBoxIndexes();
        if (!claimable?.ready) {
          return { skipped: true, reason: claimable?.reason || "角色状态缓存未启用" };
        }
        if (claimable.indexes.length === 0) {
          return { skipped: true, reason: "当前没有可领取的日常活跃宝箱" };
        }
        let rewardEntries = 0;
        for (const index of claimable.indexes) {
          const response = await protocol.send("Task_AwardDailyBox.request", { index });
          const payload = findResponse(response, 502);
          rewardEntries += Array.isArray(payload.rewards) ? payload.rewards.length : 0;
          stateStore.markDailyBoxAwarded(index);
        }
        return {
          claimedBoxIndexes: claimable.indexes,
          rewardEntries,
          activePoint: claimable.activePoint,
        };
      },
    },
    {
      id: "wipeBattle",
      label: "指定副本扫荡",
      async run(config) {
        const battleId = String(config.wipeBattle?.battleId || "").trim();
        const times = Number(config.wipeBattle?.times || 0);
        if (!/^\d+$/.test(battleId) || battleId === "0") {
          return { skipped: true, reason: "未配置有效的副本 Battle ID" };
        }
        if (!Number.isInteger(times) || times < 1 || times > 10) {
          return { skipped: true, reason: "扫荡次数必须是 1–10 的整数" };
        }
        const response = await protocol.send("Battle_WipeOut.request", { battleId, times });
        const payload = findResponse(response, 228);
        return {
          battleId,
          times,
          rewardEntries: Array.isArray(payload.reward) ? payload.reward.length : 0,
        };
      },
    },
  ];
}

class CadTaskScheduler {
  constructor(options) {
    this.protocol = options.protocol;
    this.config = options.config;
    this.onUpdate = options.onUpdate || (() => {});
    this.stateStore = options.stateStore;
    this.taskDefinitions =
      options.taskDefinitions || createTaskDefinitions(this.protocol, this.stateStore, { now: options.now });
    this.timer = null;
    this.running = false;
    this.inProgress = false;
    this.nextRunAt = null;
    this.lastRun = null;
    this.history = [];
  }

  get intervalMs() {
    return Math.max(60, Number(this.config.intervalSeconds || 3600)) * 1000;
  }

  snapshot() {
    return {
      running: this.running,
      inProgress: this.inProgress,
      nextRunAt: this.nextRunAt,
      lastRun: this.lastRun,
      history: this.history.slice(0, 20),
      enabledTasks: this.taskDefinitions
        .filter((task) => Boolean(this.config.tasks?.[task.id]))
        .map((task) => ({ id: task.id, label: task.label })),
    };
  }

  notify() {
    this.onUpdate(this.snapshot());
  }

  scheduleNext() {
    if (!this.running) return;
    clearTimeout(this.timer);
    this.nextRunAt = new Date(Date.now() + this.intervalMs).toISOString();
    this.timer = setTimeout(async () => {
      await this.runOnce("schedule");
      this.scheduleNext();
    }, this.intervalMs);
    this.timer.unref?.();
    this.notify();
  }

  async start(runNow = true) {
    if (!this.running) {
      this.running = true;
      this.notify();
    }
    if (runNow) await this.runOnce("start");
    this.scheduleNext();
    return this.snapshot();
  }

  stop() {
    this.running = false;
    this.nextRunAt = null;
    clearTimeout(this.timer);
    this.timer = null;
    this.notify();
    return this.snapshot();
  }

  async runOnce(source = "web") {
    if (this.inProgress) {
      return { status: "skipped", reason: "automation run already in progress" };
    }
    this.inProgress = true;
    this.notify();
    const startedAt = new Date().toISOString();
    const results = [];
    for (const task of this.taskDefinitions) {
      if (!this.config.tasks?.[task.id]) continue;
      const taskStartedAt = new Date().toISOString();
      try {
        const detail = await task.run(this.config);
        results.push({
          id: task.id,
          label: task.label,
          status: detail?.skipped ? "skipped" : "completed",
          startedAt: taskStartedAt,
          finishedAt: new Date().toISOString(),
          detail,
        });
      } catch (error) {
        results.push({
          id: task.id,
          label: task.label,
          status: "failed",
          startedAt: taskStartedAt,
          finishedAt: new Date().toISOString(),
          errorCode: error.code || null,
          message: error.message,
        });
      }
    }
    this.inProgress = false;
    this.lastRun = {
      source,
      status: results.some((item) => item.status === "failed") ? "partial" : "completed",
      startedAt,
      finishedAt: new Date().toISOString(),
      results,
    };
    this.history.unshift(this.lastRun);
    this.history = this.history.slice(0, 20);
    this.notify();
    return this.lastRun;
  }
}

module.exports = {
  CadTaskScheduler,
  createTaskDefinitions,
  summarizeMail,
  getShanghaiEnergyWindow,
};
