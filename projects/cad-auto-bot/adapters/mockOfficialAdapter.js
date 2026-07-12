function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function loginAuto(config) {
  const login = config.autoLogin;
  await wait(250);

  if (!login.account) {
    return {
      ok: false,
      status: "failed",
      message: "缺少账号，无法执行自动登录",
    };
  }

  return {
    ok: true,
    status: "loggedIn",
    message: "Mock 正式服适配器已完成自动登录流程",
    session: {
      account: login.account,
      serverId: login.serverId || "default",
      roleId: login.roleId || null,
      roleIndex: login.roleIndex,
      loginMode: login.mode,
      enteredGame: login.enterGame,
      issuedAt: new Date().toISOString(),
    },
  };
}

module.exports = {
  loginAuto,
};
