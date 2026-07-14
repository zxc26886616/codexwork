function trimSlash(value) {
  return String(value || "").replace(/\/+$/, "");
}

function leadSlash(value) {
  const text = String(value || "");
  return text.startsWith("/") ? text : `/${text}`;
}

function buildUrl(baseUrl, apiPath) {
  return `${trimSlash(baseUrl)}${leadSlash(apiPath)}`;
}

async function postJson(url, token, payload, timeoutSeconds) {
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), Math.max(1, timeoutSeconds) * 1000);

  try {
    const response = await fetch(url, {
      method: "POST",
      signal: controller.signal,
      headers: {
        "Content-Type": "application/json",
        Authorization: `Bearer ${token}`,
      },
      body: JSON.stringify(payload),
    });

    const text = await response.text();
    let data = null;
    if (text) {
      try {
        data = JSON.parse(text);
      } catch {
        data = { raw: text };
      }
    }

    if (!response.ok) {
      return {
        ok: false,
        status: "failed",
        message: `official API request failed: HTTP ${response.status}`,
      };
    }

    return {
      ok: true,
      status: "ok",
      response: data,
    };
  } finally {
    clearTimeout(timeout);
  }
}

async function loginAuto(config, context = {}) {
  const execution = config.execution;
  const api = execution.officialApi || {};
  const credentials = context.credentials || {};

  if (credentials.userId && credentials.channelToken) {
    return {
      ok: true,
      status: "channelAuthenticated",
      message: "已接收 SDK 签发的草花渠道凭证",
      privateSession: {
        userId: credentials.userId,
        channelToken: credentials.channelToken,
        sdkParam: credentials.sdkParam,
        source: "issuedToken",
      },
    };
  }

  if (!api.baseUrl) {
    return {
      ok: false,
      status: "failed",
      message: "officialApi.baseUrl is required",
    };
  }

  if (!context.botToken) {
    return {
      ok: false,
      status: "failed",
      message: "CAD_BOT_TOKEN is required",
    };
  }

  if (!credentials.username || !credentials.password) {
    return {
      ok: false,
      status: "failed",
      message: "草花账号和密码不能为空",
    };
  }

  let channelProfile = null;
  if (Number(config.androidChannel?.configId || 0) > 0) {
    channelProfile = findAndroidChannelProfile(
      config.target.unityProjectPath,
      config.androidChannel.configId,
    );
    if (!channelProfile) {
      return {
        ok: false,
        status: "failed",
        message: `Channel.json 中不存在渠道配置 ${config.androidChannel.configId}`,
      };
    }
  }

  const loginPayload = {
    username: credentials.username,
    password: credentials.password,
    clientName: config.target.clientName,
    requestedAt: new Date().toISOString(),
  };
  if (channelProfile) loginPayload.androidChannel = channelProfile;

  const loginResult = await postJson(
    buildUrl(api.baseUrl, api.loginPath),
    context.botToken,
    loginPayload,
    api.timeoutSeconds || 15,
  );

  if (!loginResult.ok) {
    return loginResult;
  }

  const data = loginResult.response?.data || loginResult.response || {};
  const userId = String(data.userId || data.userid || data.uid || "").trim();
  const channelToken = String(data.token || data.channelToken || "").trim();
  if (!userId || !channelToken) {
    return {
      ok: false,
      status: "failed",
      message: "草花登录接口未返回 userId/token，请按实际官方契约调整字段映射",
    };
  }

  let sdkParam = String(data.sdkParam || "").trim();
  if (!sdkParam && data.extensionJson !== undefined) {
    sdkParam = buildAndroidSdkParam({
      appId: data.appId || channelProfile?.appId,
      channelId: data.channelId || channelProfile?.channelId,
      channelApplyId: data.channelApplyId || channelProfile?.channelApplyId,
      extensionJson: data.extensionJson,
    });
  }

  return {
    ok: true,
    status: "channelAuthenticated",
    message: "草花账号登录成功，渠道凭证已保存在本机内存",
    privateSession: {
      userId,
      channelToken,
      sdkParam,
      source: "accountPassword",
    },
  };
}

module.exports = {
  loginAuto,
};
const {
  buildAndroidSdkParam,
  findAndroidChannelProfile,
} = require("../protocol/cadAndroidSdkParam");
