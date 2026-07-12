/**
 * 正式服适配器模板。
 *
 * 接入原则：
 * - 只调用你们自己正式服允许的后台/API 能力。
 * - 使用服务端签发的 bot token、白名单账号或内部测试账号。
 * - 不绕过验证码、风控、反作弊、支付、封禁、排队和权限校验。
 * - 所有请求必须可审计、可限流、可撤销。
 */

async function loginAuto(config) {
  const login = config.autoLogin;

  // TODO: 在这里接入你们正式服允许的登录 API。
  // 示例流程：
  // 1. 用 botToken 调用内部授权接口换取短期 session。
  // 2. 根据 serverId 选择区服。
  // 3. 根据 mode 选择最近角色、指定 roleId 或 roleIndex。
  // 4. 如果 enterGame=true，调用正式的进入游戏接口。
  // 5. 返回结构化结果给本地控制台。

  throw new Error(`official adapter is not implemented for account: ${login.account || "<empty>"}`);
}

module.exports = {
  loginAuto,
};
