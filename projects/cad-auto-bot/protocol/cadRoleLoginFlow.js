function findResponse(response, tag) {
  const message = (response.messages || []).find(
    (item) => Number(item.tag) === Number(tag) && item.rpcType === "RESPONSE",
  );
  if (!message) throw new Error(`Game protocol response is missing for tag ${tag}`);
  if (message.error) throw new Error(`Game protocol returned an error for tag ${tag}`);
  return message.payload || {};
}

function selectRole(rolePayload, options) {
  const roles = Object.values(rolePayload.roleInfos || {}).filter(
    (role) => !options.serverNode || role.gameNode === options.serverNode,
  );
  if (roles.length === 0) throw new Error("No role is available on the selected server");

  if (options.mode === "roleId") {
    const selected = roles.find((role) => String(role.rid) === String(options.roleId));
    if (!selected) throw new Error("The configured roleId was not found on the selected server");
    return selected;
  }

  if (options.mode === "roleIndex") {
    const sorted = [...roles].sort(
      (left, right) => Number(left.createTime || 0) - Number(right.createTime || 0),
    );
    const selected = sorted[Number(options.roleIndex || 0)];
    if (!selected) throw new Error("The configured roleIndex is outside the role list");
    return selected;
  }

  return roles.reduce((latest, role) =>
    Number(role.lastLoginTime || 0) > Number(latest.lastLoginTime || 0) ? role : latest,
  );
}

function decodeSdkParam(sdkParam) {
  try {
    return JSON.parse(Buffer.from(sdkParam, "base64").toString("utf8"));
  } catch {
    throw new Error("sdkParam is not valid Base64 JSON");
  }
}

async function loginSelectedRole(options) {
  const roleListResponse = await options.protocol.send("Role_GetRoleList.request", {
    codeVersion: options.codeVersion,
    gameNode: options.serverNode,
  });
  const rolePayload = findResponse(roleListResponse, 1);
  if (options.mode === "accountOnly") {
    return { roleCount: Object.keys(rolePayload.roleInfos || {}).length, selectedRole: null };
  }

  const selectedRole = selectRole(rolePayload, options);
  const sdk = decodeSdkParam(options.sdkParam);
  const roleLoginResponse = await options.protocol.send("Role_RoleLogin.request", {
    rid: String(selectedRole.rid),
    ip: options.ip || "",
    phone: options.deviceModel || "Android",
    area: options.area || "",
    language: Number(options.language || 0),
    platform: 2,
    version: options.clientVersion || "",
    adid: options.userId,
    isReConnect: false,
    belongId: options.belongId || "1002984",
    channelCpId: String(sdk.channelApplyId || ""),
    codeVersion: options.codeVersion,
    appId: String(sdk.appId || ""),
  });
  const loginPayload = findResponse(roleLoginResponse, 3);
  if (loginPayload.hasResult && !loginPayload.result) {
    throw new Error("Role login was rejected by the game server");
  }
  return {
    roleCount: Object.keys(rolePayload.roleInfos || {}).length,
    selectedRole: {
      rid: String(selectedRole.rid),
      name: selectedRole.name || "",
      gameNode: selectedRole.gameNode || "",
    },
    loginResult: Boolean(loginPayload.result),
  };
}

module.exports = {
  decodeSdkParam,
  findResponse,
  loginSelectedRole,
  selectRole,
};
