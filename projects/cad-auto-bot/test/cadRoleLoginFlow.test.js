const test = require("node:test");
const assert = require("node:assert/strict");
const { loginSelectedRole, selectRole } = require("../protocol/cadRoleLoginFlow");

const roles = {
  "9007199254740993": {
    rid: "9007199254740993",
    name: "older",
    gameNode: "game1",
    createTime: "10",
    lastLoginTime: "100",
  },
  "9007199254740995": {
    rid: "9007199254740995",
    name: "latest",
    gameNode: "game1",
    createTime: "20",
    lastLoginTime: "200",
  },
};

test("role selection preserves 64-bit ids and supports last role", () => {
  assert.equal(selectRole({ roleInfos: roles }, { serverNode: "game1" }).rid, "9007199254740995");
  assert.equal(
    selectRole(
      { roleInfos: roles },
      { serverNode: "game1", mode: "roleId", roleId: "9007199254740993" },
    ).rid,
    "9007199254740993",
  );
});

test("role login sends the fields used by the Android client", async () => {
  const requests = [];
  const protocol = {
    async send(type, fields) {
      requests.push({ type, fields });
      if (type === "Role_GetRoleList.request") {
        return { messages: [{ tag: 1, rpcType: "RESPONSE", payload: { roleInfos: roles } }] };
      }
      return {
        messages: [{ tag: 3, rpcType: "RESPONSE", payload: { hasResult: true, result: true } }],
      };
    },
  };
  const result = await loginSelectedRole({
    protocol,
    codeVersion: "1.0.11",
    serverNode: "game1",
    mode: "roleId",
    roleId: "9007199254740993",
    sdkParam: Buffer.from(JSON.stringify({ appId: "app", channelApplyId: "apply" })).toString("base64"),
    userId: "user",
  });

  assert.equal(requests[1].fields.rid, "9007199254740993");
  assert.equal(requests[1].fields.platform, 2);
  assert.equal(requests[1].fields.appId, "app");
  assert.equal(requests[1].fields.channelCpId, "apply");
  assert.equal(result.selectedRole.rid, "9007199254740993");
});
