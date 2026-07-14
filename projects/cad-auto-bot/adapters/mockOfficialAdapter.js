function wait(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function loginAuto(config, context = {}) {
  const login = config.autoLogin;
  const credentials = context.credentials || {};
  await wait(250);

  if (!(credentials.username && credentials.password) && !(credentials.userId && credentials.channelToken)) {
    return {
      ok: false,
      status: "failed",
      message: "缺少草花登录凭证",
    };
  }

  const userId = credentials.userId || `mock-${credentials.username}`;
  return {
    ok: true,
    status: "channelAuthenticated",
    message: "Mock 草花登录成功，渠道凭证已保存在本机内存",
    privateSession: {
      userId,
      channelToken: credentials.channelToken || "mock-channel-token",
      sdkParam:
        credentials.sdkParam ||
        Buffer.from(
          JSON.stringify({
            appId: "mock-app",
            channelId: "mock-channel",
            channelApplyId: "mock-apply",
            extensionJson: "",
          }),
        ).toString("base64"),
      source: credentials.userId ? "issuedToken" : "accountPassword",
    },
  };
}

module.exports = {
  loginAuto,
};
