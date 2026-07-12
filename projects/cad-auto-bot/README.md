# CAD 自动挂机控制台

这是给 `D:\code\cad_client\cadclient3.0` Unity 项目准备的本地自动挂机控制台。当前第一版先实现正式服方向的自动登录控制台框架：

- 本地 HTTP 服务器，默认监听 `http://127.0.0.1:17830`。
- Web 页面显示和编辑自动登录参数。
- Web 页面可以发起一次自动登录命令。
- 本地服务器直接调用“正式服适配器”执行自动登录流程。
- 默认启用 `dryRun` 和 Mock 适配器，避免误触正式服。

## 运行

```bash
cd D:\code\codexwork\projects\cad-auto-bot
node server.js
```

浏览器打开：

```text
http://127.0.0.1:17830
```

## 正式服接入

1. 在 Web 面板里把“执行适配器”切换为 `正式服适配器`。
2. 填写内部授权用的 `Bot Token`。
3. 填写正式服 API Base URL、登录接口路径、进服接口路径。
4. 确认账号白名单、限流、审计和权限已经在服务端生效。
5. 关闭 `dryRun 只演练` 后再执行自动登录。

默认 `adapters/officialAdapter.js` 会向登录接口发送账号、区服、角色选择等参数；如果你们正式服协议字段不同，可以在这个文件里做字段映射。

这个工具不做客户端外挂、反作弊绕过、验证码绕过或模拟隐蔽玩家输入；正式服能力应通过你们拥有权限的服务端接口接入。

## 自动登录参数

参数保存在 `data/config.json`：

- `execution.adapter`: `mock` 或 `official`。
- `execution.environment`: `production`、`staging`、`dev`。
- `execution.productionGuard`: 正式服保护开关。
- `execution.dryRun`: 只创建命令和记录参数，不调用执行适配器。
- `execution.botToken`: 内部授权令牌。
- `execution.officialApi.baseUrl`: 正式服授权 API 地址。
- `execution.officialApi.loginPath`: 自动登录接口路径。
- `execution.officialApi.enterGamePath`: 进入游戏接口路径。
- `execution.officialApi.timeoutSeconds`: HTTP 请求超时秒数。
- `mode`: `lastRole`、`roleId`、`roleIndex`、`accountOnly`。
- `account` / `password`: 本地测试账号参数，可留空。
- `serverId`: 目标服务器 ID，可留空。
- `roleId`: 指定角色 ID。
- `roleIndex`: 指定角色序号，从 0 开始。
- `enterGame`: 选角后是否进入游戏。
- `delaySeconds`: 自动登录命令执行前的延迟。
- `retryTimes` / `retryIntervalSeconds`: 后续接入真实登录流程时使用。

## API

- `GET /api/status`: 查看配置、服务器状态、最近执行和日志。
- `GET /api/config`: 查看配置。
- `PUT /api/config`: 保存配置。
- `POST /api/login/request`: 创建一次自动登录命令。
