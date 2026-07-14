const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const {
  buildAndroidSdkParam,
  decodeAndroidSdkParam,
  findAndroidChannelProfile,
  loadAndroidChannelProfiles,
} = require("../protocol/cadAndroidSdkParam");

function createChannelFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "cad-channel-"));
  const configDir = path.join(root, "Config", "Json");
  fs.mkdirSync(configDir, { recursive: true });
  fs.writeFileSync(
    path.join(configDir, "Channel.json"),
    JSON.stringify({
      mDataMap: {
        2: {
          ID: 2,
          appId: "1214",
          channelId: "1017",
          channelApplyId: "apply-2",
          marketType1: 29,
        },
      },
    }),
  );
  return root;
}

test("Android 渠道配置读取和 sdkParam 格式与客户端一致", () => {
  const root = createChannelFixture();
  assert.deepEqual(loadAndroidChannelProfiles(root), [
    {
      id: 2,
      appId: "1214",
      channelId: "1017",
      channelApplyId: "apply-2",
      marketType: 29,
    },
  ]);
  assert.equal(findAndroidChannelProfile(root, 2).channelApplyId, "apply-2");

  const sdkParam = buildAndroidSdkParam({
    appId: "1214",
    channelId: "1017",
    channelApplyId: "apply-2",
    extensionJson: '{"device":"test"}',
  });
  assert.deepEqual(decodeAndroidSdkParam(sdkParam), {
    appId: "1214",
    channelId: "1017",
    channelApplyId: "apply-2",
    extensionJson: '{"device":"test"}',
  });
});
