# CAD 自动挂机控制台

这是为 `D:\code\cad-client-mas\cadclient3.0_unity2021\cadclient3.0_unity2021` 准备的本地 Web 控制台。当前主登录链路已改为安卓草花账号体系。

当前已实现：

- 本地 Web 控制台，默认监听 `http://127.0.0.1:17830`。
- 草花账号密码一次性提交；密码不写配置、不进日志、不回显。
- 可直接输入安卓 SDK 已签发的 `userId/token` 做联调；未进入角色网关的 token 最多在进程内存保存 30 分钟。
- 已授权的草花登录代理适配器，将账号密码换成游戏实际需要的 `userId/token`。
- 游戏登录网关的 DH、HMAC64、DES 和二字节包头实现，固定向量与 Unity `Crypt2.cs` 一致。
- 直接复用客户端生成的 `Sproto.cs` 完成角色列表、角色登录和 GateMessage 双层编解码。
- 角色 ID 全程按 64 位整数安全传递；登录后每 15 秒心跳，并自动响应服务器 Ping。
- 角色登录成功后转为持续在线会话，并清除内存中的草花 token/sdkParam；可在 Web 主动断开。
- Web 展示最近成功心跳；连续三次心跳失败会自动停止调度并释放失效连接。
- 网关异常断开后按指数退避自动重连，成功后恢复原先运行中的挂机调度。
- 重连材料使用进程启动时随机生成的 AES-GCM 密钥密封，只存在于内存且不会写入状态 API。
- Web 可启动、停止或立即执行挂机任务；支持邮件、普通签到、定时体力、日常任务、活跃宝箱和指定副本扫荡。
- 默认启用 `dryRun` 和 Mock 适配器，避免误触正式服务。

## 运行

```powershell
cd D:\code\codexwork\projects\cad-auto-bot
$env:CAD_BOT_TOKEN="你们自有登录代理的内部调用令牌"
npm run build:bridge
node server.js
```

浏览器打开 `http://127.0.0.1:17830`。

## 草花账号登录代理契约

Unity 工程中的安卓入口只调用无参数的 `CHGameManager.Login()`。账号密码界面由原生草花 SDK 管理，客户端成功回调包含 `userId`、`userName`、`token`、`extensionJson` 和 `regType`。仓库中没有安卓 SDK 本体，也没有公开的账号密码 HTTP 契约，因此本工具不会猜测草花私有接口。

请将 Web 面板中的“草花登录代理 Base URL”和“账号登录接口路径”指向你们获授权的服务端接口。当前适配器发送：

```json
{
  "username": "玩家输入的草花账号",
  "password": "玩家本次输入的密码",
  "clientName": "CAD Client 3.0",
  "requestedAt": "ISO-8601 时间"
}
```

请求头使用 `Authorization: Bearer <CAD_BOT_TOKEN>`。响应支持以下两种结构：

```json
{ "userId": "...", "token": "...", "sdkParam": "可选的本次安卓 SDK Base64 参数" }
```

```json
{ "data": { "userId": "...", "token": "...", "sdkParam": "..." } }
```

Android 客户端的 `sdkParam` 已确认是以下 JSON 的 UTF-8 Base64：

```json
{
  "appId": "...",
  "channelId": "...",
  "channelApplyId": "...",
  "extensionJson": "..."
}
```

Web 会读取目标工程的 `Config/Json/Channel.json` 展示渠道候选项。明确选择渠道后，登录代理也可以只返回：

```json
{ "userId": "...", "token": "...", "extensionJson": "..." }
```

控制台会使用所选渠道的前三个字段生成与 `SDKAndroidPlugin.GetLoginParam()` 一致的 Base64。渠道配置不能从候选列表中猜测；不确定当前安装包时应保持“由登录代理返回完整 sdkParam”。

只取得草花渠道凭证时 `sdkParam` 可以省略；启用“草花登录后连接游戏登录服”时必须由代理完整返回，或由所选渠道与代理返回的 `extensionJson` 生成。使用 SDK 已签发凭证联调时，可在页面展开区域一次性输入 `sdkParam`，该字段同样不会落盘。

若官方契约还要求设备标识、渠道签名、验证码或实名校验，应在获授权的登录代理中按官方文档实现，不应在本地控制台绕过。

协议桥使用 .NET 10 编译，并直接链接目标 Unity 工程中的 Sproto 运行时与生成协议；Unity 协议更新后需重新执行 `npm run build:bridge`。

## 游戏服登录链路

Web 控制台可以按需加载客户端使用的草花区服目录。目录请求只允许客户端已有的固定 HTTPS 地址与 `release` / `develop` 环境，不接受任意远程 URL。加载后仍需人工明确选择区服，维护中、未开放和内网区服不可选；选择会填入现有 `serverUrl`、`serverIP`、`port`、`serverNode` 与服务器 ID，手工配置方式继续保留。

完成一次草花渠道登录后，可以点击“查询账号角色区服”。本地服务仅在内存中使用渠道 `userId` 调用 Android 客户端固定的 `queryHaveRole` 接口，再按 `serverNode` 与官方区服目录匹配；浏览器不会收到明文账号 ID 或请求签名。查询结果仍需人工明确选择，不会自动切服或自动保存。

如果首次登录时尚未启用角色网关，可以先查询并明确选择区服、勾选角色网关，再点击“连接所选区服”。该动作会先保存当前表单，然后复用内存中的草花渠道凭证完成登录服和角色网关连接，不需要再次提交账号密码；已连接的会话不允许直接切服。

渠道登录成功后，工具在内存中持有 `userId/token`。游戏网关登录 token 的客户端格式已确认是：

```text
userId:channelToken:platform:language::serverNode:1:sdkParam
```

安卓客户端参数已确认：`platform` 为 `2`，第四段为 `ANDROID`；`sdkParam` 是包含 `appId/channelId/channelApplyId/extensionJson` 的 Base64 JSON。`serverNode` 和认证 WebSocket 地址来自所选区服配置，客户端连接格式为 `serverUrl?host=serverIP&port=port&rand=时间戳`。

登录服 WebSocket challenge、DH、HMAC64、DES token 和 200 跳转解析已经接入控制台。开启“选角后进入游戏”时，会连接登录服下发的角色网关，完成重定向认证，然后发送 `Role_GetRoleList` 和 `Role_RoleLogin`。角色选择支持最近登录角色、指定角色 ID 和按创建时间排序的角色序号。登录成功后持续发送 `Role_Heart`，收到 `Role_Ping` 时回复 `Role_Pong`。真实连接默认由开关和 `dryRun` 共同保持关闭。

角色网关意外关闭或连续三次心跳失败时，默认最多自动重连 5 次，基础间隔 5 秒并指数退避到最多 60 秒。主动点击“断开角色连接”或本地服务退出不会触发重连。

客户端当前仓库配置值为 `codeVersion=1.0.11`、`clientVersion=v1.0.24`，已作为 Web 面板默认值；正式连接前应与目标服务器发布版本核对。当前不自动创建角色。角色网关同时兼容末尾带明文 game session/标志的新帧，以及 MAS 中 `is_compressed` 分支使用的整包 DES 旧帧；该分支与客户端一致，不执行 gzip/LZ4 解压。

## 挂机任务

挂机调度只在角色登录成功后创建。未进入游戏的渠道凭证 30 分钟过期；角色成功登录后由心跳维持持续会话，直到 Web 主动断开、网关断线或本地服务退出。默认总开关关闭、执行间隔 60 分钟：

- 角色邮件：发送 `Email_TakeEnclosure`，`mailType=1`、空 `indexs`，与客户端“一键领取”一致。
- 账号邮件：发送 `Email_TakeEnclosure`，`mailType=2`、空 `indexs`。
- 每日普通签到：发送 `Welfare_SignDay`，三个补签/消耗标记均为 `false`；默认单项关闭。
- 定时体力：上海时区 12:00–14:00、19:00–21:00 分别发送一次 `Welfare_PickEnergy`；只有角色体力未满且服务器功能次数可用时才执行。默认关闭，绝不调用 `Welfare_RePickEnergy`、广告、VIP 或道具补领。
- 已完成日常任务：监听服务器 `Task_DailyTask` 推送，结合客户端 `Task.json` 的完成条件生成可领取 ID，再发送 `Task_TaskFinish(taskType=3)`；默认关闭。
- 日常活跃宝箱：按已领取日常任务累计活跃度，仅领取达到 `50/100/150` 且未出现在角色 `dailyAwarded` 中的 `1/2/3` 号宝箱；默认关闭。
- 指定副本扫荡：发送客户端已有的 `Battle_WipeOut`，Battle ID 由开发者明确配置，每轮限制 `1–10` 次；默认关闭。

Web API：

- `POST /api/automation/start`：立即执行一次并启动周期调度。
- `POST /api/automation/run`：仅立即执行一次。
- `POST /api/automation/stop`：停止周期调度。
- `POST /api/session/disconnect`：停止调度和心跳，关闭角色网关并清理内存会话。

每个任务独立记录结果与服务器错误码，一个任务失败不会阻断后续任务。日常状态未收到或本地任务配置读取失败时会标记“跳过”，不会盲发领取协议。扫荡依赖服务器校验副本解锁、剩余次数、背包和战力条件；工具不会自动购买次数、恢复体力或扩大到实时战斗。客户端现有生成协议中也没有可直接复用的离线挂机奖励接口，因此未伪造该能力。

`D:\code\cad-client-mas` 仅作为只读协议和调用链参考，本工具不得修改、构建或注入该客户端。实时战斗层大量依赖 Unity 场景、物理、动画和固定帧逻辑，部分单人关卡还上传帧数据校验；在“不改客户端”的边界下，独立 Node 工具只接入服务端明确提供的协议能力，不能伪造胜利结算，也不会提供依赖客户端改造的 Worker 功能。

## 检查

```powershell
npm run check
npm test
```

## 安全边界

- `D:\code\cad-client-mas` 永久按只读源码参考处理，只读取渠道配置、任务配置和协议定义；禁止写文件、生成资源、构建改造版客户端或注入启动代码。
- 页面不会把密码或渠道 token 保存进 `data/config.json`。
- 命令对象、运行状态和日志只保留脱敏后的草花 userId。
- 不从 APK/AAR 反推或仿造草花私有账号协议，不绕过验证码、实名、风控或封禁。
- 正式环境应由自有代理完成白名单、限流、审计和权限控制。
