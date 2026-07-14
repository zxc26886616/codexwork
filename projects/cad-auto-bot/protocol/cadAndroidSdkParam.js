const fs = require("node:fs");
const path = require("node:path");

function loadAndroidChannelProfiles(unityProjectPath) {
  const configPath = path.join(unityProjectPath, "Config", "Json", "Channel.json");
  const config = JSON.parse(fs.readFileSync(configPath, "utf8"));
  return Object.values(config.mDataMap || {})
    .map((item) => ({
      id: Number(item.ID),
      appId: String(item.appId || ""),
      channelId: String(item.channelId || ""),
      channelApplyId: String(item.channelApplyId || ""),
      marketType: Number(item.marketType1 || 0),
    }))
    .filter(
      (item) =>
        item.id > 0 &&
        !(item.appId === "1" && item.channelId === "1" && item.channelApplyId === "1"),
    )
    .sort((left, right) => left.id - right.id);
}

function findAndroidChannelProfile(unityProjectPath, configId) {
  const id = Number(configId || 0);
  if (!id) return null;
  return loadAndroidChannelProfiles(unityProjectPath).find((item) => item.id === id) || null;
}

function buildAndroidSdkParam(fields) {
  const payload = {
    appId: String(fields.appId || "").trim(),
    channelId: String(fields.channelId || "").trim(),
    channelApplyId: String(fields.channelApplyId || "").trim(),
    extensionJson: String(fields.extensionJson || ""),
  };
  if (!payload.appId || !payload.channelId || !payload.channelApplyId) {
    throw new Error("生成 Android sdkParam 需要 appId/channelId/channelApplyId");
  }
  return Buffer.from(JSON.stringify(payload), "utf8").toString("base64");
}

function decodeAndroidSdkParam(sdkParam) {
  const payload = JSON.parse(Buffer.from(String(sdkParam || ""), "base64").toString("utf8"));
  return {
    appId: String(payload.appId || ""),
    channelId: String(payload.channelId || ""),
    channelApplyId: String(payload.channelApplyId || ""),
    extensionJson: String(payload.extensionJson || ""),
  };
}

module.exports = {
  buildAndroidSdkParam,
  decodeAndroidSdkParam,
  findAndroidChannelProfile,
  loadAndroidChannelProfiles,
};
