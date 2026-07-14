const test = require("node:test");
const assert = require("node:assert/strict");
const { CadSecretVault, reconnectDelayMs } = require("../automation/cadSecretVault");

test("重连凭证只以进程内 AES-GCM 密文保存", () => {
  const vault = new CadSecretVault(Buffer.alloc(32, 7));
  const secret = {
    userId: "caohua-user",
    channelToken: "very-secret-token",
    sdkParam: "very-secret-sdk-param",
  };
  const sealed = vault.seal(secret);

  assert.deepEqual(vault.open(sealed), secret);
  assert.equal(JSON.stringify(sealed).includes(secret.channelToken), false);
  assert.equal(JSON.stringify(sealed).includes(secret.sdkParam), false);
  vault.clear();
  assert.throws(() => vault.open(sealed));
});

test("自动重连使用有上限的指数退避", () => {
  assert.equal(reconnectDelayMs(1, 5), 5000);
  assert.equal(reconnectDelayMs(2, 5), 10000);
  assert.equal(reconnectDelayMs(4, 5), 40000);
  assert.equal(reconnectDelayMs(8, 5), 60000);
});
