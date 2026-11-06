# 海盐折纸桌面助手

## 当前交付（2026-11-06）

Windows 桌面助手已实现普通 BGA 的「开始游戏 → 即时制」匹配、双人基础版席位核对、己方自动出牌、读取官方结算和按批次续排。教程、单桌、热座连局和战绩统计也可使用。达到喊停条件时，两个按钮都合法就选「再给一次机会」；它缺失或禁用时才选合法的「立即结束」。

| 项目 | 已核实的结果 |
| --- | --- |
| 固定候选 | `1.3.2-p0-bf8907fa7ce665f7`，清单 SHA-256 为 `C0486F8268849E3000800F45EEEE28C1339F6E8234560D4D96C43329ABC772B9`。 |
| 防超时与防重发 | 同桌同席才行动；回执不明先重读服务端状态。桌 943515895 的 8 个原案场景全部通过，恢复时旧的结束回合零重发。完整 P0 回归 75/75，其中无合法出牌的场景验证停排并请求接管。 |
| 真人结果 | 台账中有 3 张独立正常完赛桌，比分按己方在前分别为 26–46、34–43、38–40。这三局跨批次；单次启动连续三局、100 局和 24 小时运行均待验收。[百局台账](evidence/2026-11-05-hundred/百局台账.json) |
| 当前阻断 | 最新三局批次中，大厅奖杯遮罩收起后再次显示「已入队」，监管报 `admission-count-conflict` 并停续排，预定三局未验收。 |

下载仓库后，将[固定包](发布附件/海盐折纸-bf8907fa.zip)解压至本项目根目录，核对 `dist/candidates/1.3.2-p0-bf8907fa7ce665f7/candidate.json` 指纹即可使用这一版。需要 Windows x64、.NET 10 Windows Desktop Runtime 和 Edge WebView2。需要查验逐项受控回执时，将[离线回归证据](发布附件/离线回归证据.7z)也解压到项目根。正式账号的运行目录和仍在使用的批次票据保留在本机。以下 `dist/1.3.0` 和 `dist/1.3.1-*` 命令对应历史独立版，在原开发机上需先将 `dist/历史独立发行版.7z` 解压至 `dist`。

独立 Windows x64 应用。1.3.0 提供单桌出牌、战绩和热座连局；1.3.1 是早期即时制版本，缺少后续 P0 保桌修复，请勿用它参与正式站真人复测。正式复测只使用[验收记录](evidence/2026-11-04-realtime-gate/验收记录.md)中明确放行的固定构建。各版同目录的 `SeatControl.exe` 提供后台命令控制，本机需要 Microsoft Edge WebView2 Runtime。

「开始游戏 → 即时制」自动匹配、己方出牌和结算后重新排队的开发计划见[海盐折纸 24 小时即时制自动匹配开发规划](../../docs/projects/海盐折纸真人实战开发规划.md)。

## 第一次使用

1. 打开「单人教程」，按自己的偏好处理 BGA 的 Cookie 提示。看到桌面窗口底部显示「教学桌已就绪」后，点「启动自动出牌」。教程使用 BGA 的预设局面，包含抽牌、弃牌、打出组合和回合决策。点「暂停自动出牌」可随时接手。
2. 要进入自己的牌桌，点「自动登录」，选择 BGA 正式站或 Studio，输入该站点的 BGA 玩家名和密码，点「保存并登录」。也可直接在页面中手动登录。单桌模式下可将完整游戏地址粘贴到地址栏，进入自己的游戏座位后点「启动自动出牌」。多桌双人友谊赛可使用「连续对局」，由助手逐桌开局。普通桌支持中英文按钮，教学入口使用中文版。
3. 对局结束后保持窗口打开，程序会读取官方排名并保存。也可粘贴一张已结束的海盐折纸桌号进行补录。单人教程的固定回放不参与胜率统计。
4. 点「对局统计」，选自己的玩家名和 ID，查看局数、独胜、并列第一、独胜率、平均得分和逐桌结果。默认列表包含你用本应用打开并收录的桌。

也可用 `SeaSaltDesk.exe 879821206` 直接打开指定桌号。窗口底部显示当前状态，牌桌右侧的「操作日志」按时间显示本次运行最近 200 条操作和状态变化。助手的状态、日志和检查结果使用中文，内嵌网页沿用 BGA 自身的语言设置。

BGA 官方介绍的完整游戏为 2–4 人。自动出牌请用于 BGA 允许的测试环境，例如获准使用的 Studio 牌桌。[游戏页](https://boardgamearena.com/gamepanel?game=seasaltpaper) · [BGA Studio 测试机器人](https://en.doc.boardgamearena.com/Bots_and_Artificial_Intelligence) · [公平竞技条款](https://en.boardgamearena.com/legal?section=tos)

## 自动登录与本机数据

- 正常启动时，助手会打开上次保存的站点，识别该站点的官方两步登录页。已有会话时核对玩家名。命令行传入桌号或链接时优先打开指定页面。
- BGA 正式站和 Studio 的凭据分别保存在当前 Windows 用户的「凭据管理器」。`%LOCALAPPDATA%\SeaSaltDesk\login-site.txt` 仅记录默认站点，`profile\` 保存 WebView2 的 Cookie 和会话。
- 更换账号或密码时，再次打开「自动登录」并保存。点「忘记此站账号」可删除所选站点保存的凭据。如还要退出当前网页会话，请在 BGA 页面中注销。
- Cookie 偏好、验证码和其他安全验证由本人在页面上完成。若使用第三方账号登录，请在页面上手动操作。密码被拒绝或页面账号不匹配时，查看助手底部提示，核对设置后再试。

## 即时制自动匹配三局

「连续对局 → 即时制连打 3 局」会在后台进入上次保存站点的「开始游戏」大厅，选普通游戏、即时制和自动对手。未保存默认站点时使用 Studio。已保存该站点账号时，遇到登录页会尝试登录一次。正式站测试由唯一实战进程执行，勿在个人游戏期间另开助手排队。

助手在《海盐折纸》的匹配设置里选择双人基础版，将其他人数、两种扩展和双倍分数设为不接受。这些偏好由 BGA 保存在该账号上。每桌按平台实际分配的桌号和座位启动己方出牌。结算收录后自动排下一桌，第三桌收录后退出。账号不一致、无法核对匹配设置或需要安全验证时，批次显示停止原因。

同一入口也可在项目 PowerShell 中启动。站点应具有该游戏并允许自动匹配测试。

```powershell
& .\dist\1.3.1-realtime\SeatControl.exe --realtime 3 --site Studio
```

本机提供的测试账号属于 BGA 正式站。项目目录的 `BGA测试账号.xml` 是加密的 PowerShell 凭据文件，可由保存时的 Windows 用户通过 `Import-Clixml` 读取。助手使用 Windows 凭据管理器中对应站点的记录。上面的 Studio 命令需要单独的 Studio 账号。

本地验证使用临时资料和 WebView2 拦截的受控页面，运行 `dotnet run --project tests/Checks.csproj -- --realtime-three`。同一后台入口会处理三次排队、两次开局确认、一次直接分桌、每桌原桌重载和官方格式结算。它会核对三张独立战绩以及第三局后没有第四次入队。

即时制将**己方超时被踢**列为 P0。确认匹配后大厅未跳转，助手会用已保存的桌号核对原桌。轮到己方时优先出牌，策略无法选择时执行页面明确允许的操作。批次分别记录己方回合和出牌次数。原桌载入超时、席位核对失败或己方回合持续无进展时，助手显示牌桌供接管。零出牌、被踢或人工代打的桌均不能作为连续三局的验收成绩。发布前需覆盖大厅丢失开局通知、策略遇到新状态、合法控件被禁用这三种情况。

发生过的三起 P0、官方结束原因和验收标准见[即时制超时事故记录](../../docs/projects/海盐折纸即时制超时事故记录.md)。P0 候选在本进程已确认分桌、尚未接席且只读页面挂起时，自动停止续排并重读原桌；身份、座位和规则吻合后接席。已有待确认动作时沿用原意图，核对服务端进展后再行动。救场桌单独记录，不计入连续三局验收。慢读保留原桌的用例随 `--realtime-three` 运行；`--realtime-void` 验证取消桌不入库、不续排。批次目录下的 `<批次号>.incidents.jsonl`、`<批次号>.fallback.jsonl` 分别记录事故与救场经过。

## 连续对局

1. 在桌面助手中登录有 Hotseat 权限的 BGA 账号，回到「牌桌」，点「连续对局」。
2. 设置 1–1000 局和第二席的本地热座名。默认 10 局，热座名为 `FoldTest`。昵称可用 1–16 个文字、数字、空格、横线或下划线。点「开始新批次」，后台会逐桌运行基础版双人友谊赛并记录结算。
3. 「BGA 回合制（本地热座）」可用于新批次，也可在「查看批次」选中已暂停的热座批次后勾选，再点「继续未完成批次」。助手保留已收录成绩和已绑定桌号，并分别显示首次转入前、转入后收录的局数。之后恢复该批次会沿用回合制。
4. 同一资料目录可同时运行一个回合制热座批次和一个即时制批次。不同任务各用自己的批次编号。可在「查看批次」中切换，查看当前桌号、已完成局数、双方独胜率与均分及最近 15 条操作。切回「对局统计」可查看后台已收录的逐桌成绩。关闭窗口后后台继续运行，批次记录保存在 `%LOCALAPPDATA%\SeaSaltDesk\series\`。

在项目目录的 PowerShell 中也可启动同样的批次。保留 `$batchId` 输出的编号，以便之后恢复。

```powershell
$seriesRoot = Join-Path $env:LOCALAPPDATA 'SeaSaltDesk'
$batchId = [guid]::NewGuid().ToString('N')
$batchId
& .\dist\1.3.0\SeatControl.exe --series 10 --guest FoldTest --batch-id $batchId --root $seriesRoot
```

已有批次中断后，在同一 PowerShell 会话中运行下列命令。若重新打开了终端，先将 `$batchId` 和 `$seriesRoot` 设为原来的值。

```powershell
& .\dist\1.3.0\SeatControl.exe --resume $batchId --root $seriesRoot
```

回合制专用包位于 `dist/1.3.1-turnbased`。从这个目录打开 `SeaSaltDesk.exe` 可使用弹窗中的速度选择。要从命令行转入回合制，先确认原热座批次已停止，再用原编号恢复。新建热座批次时，`--series` 也可搭配 `--speed turnbased`。

```powershell
& .\dist\1.3.1-turnbased\SeatControl.exe --resume $batchId --root $seriesRoot --speed turnbased
```

例如，原计划 10 局、转入前已收录 3 局，这 3 局保留在历史分段。若第 4 桌已开局，助手会沿用原桌号继续。该桌结算后计入转入后收录的 7 局。

## 后台命令控制

`SeatControl.exe` 在独立的后台 WebView2 中操作牌桌，不需要移动鼠标或切换当前窗口。它通过标准输入和标准输出通信，每行是一个 UTF-8 JSON 对象。控制程序应持续保持进程和输入管道打开。无参数时使用桌面助手的本机目录和网页会话；`--root <目录>` 可另设独立的网页资料与战绩目录，`--url <地址或桌号>` 指定起始页。

在项目目录的 PowerShell 7 中可用以下命令打开后台大厅并读取状态。默认复用助手已登录的网页会话。

```powershell
$seatInfo = [Diagnostics.ProcessStartInfo]::new((Resolve-Path 'dist/1.3.0/SeatControl.exe').Path)
$seatInfo.UseShellExecute = $false
$seatInfo.CreateNoWindow = $true
$seatInfo.RedirectStandardInput = $true
$seatInfo.RedirectStandardOutput = $true
$seatInfo.StandardInputEncoding = $seatInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$seatInfo.ArgumentList.Add('--url')
$seatInfo.ArgumentList.Add('https://boardgamearena.com/lobby')
$seat = [Diagnostics.Process]::Start($seatInfo)
function Send-SeatCommand($op, $arg = '') {
    $seat.StandardInput.WriteLine((@{op=$op;arg=$arg} | ConvertTo-Json -Compress))
    $seat.StandardInput.Flush()
    $seat.StandardOutput.ReadLine() | ConvertFrom-Json
}
Send-SeatCommand state
```

| 命令 | 用法 |
| --- | --- |
| `state`、`log` | `state` 返回当前地址、中文状态、出牌按钮可用状态 `enabled` 和运行状态 `running`。`log` 读取本次运行的操作记录。 |
| `navigate` | 传入 BGA HTTPS 地址或桌号。收到回执后用 `state` 查看实际落点和加载状态。 |
| `inspect`、`list` | `inspect` 读取当前大厅或牌桌提示，传 `牌局` 查看行动状态，传 `结算` 查看最终页面上两侧可核对的原始名次和得分栏。`list` 以关键词筛选可见控件。 |
| `click`、`select`、`fill` | 使用最近一次 `list` 返回的「编号」。点击传编号，下拉选择传 `编号\|选项值`，普通文本框传 `编号\|文本`。切页或控件变化后重新列出。 |
| `autoplay` | 牌桌就绪后运行 `Send-SeatCommand autoplay start`，暂停用 `stop`。调用后查看 `state`、`log` 确认进展。 |
| `quit` | `Send-SeatCommand quit` 退出后台进程。关闭输入管道也会结束进程。 |

在玩家站大厅选「与朋友游戏」→「同屏游玩」，给第二席设置本桌称呼。等大厅明确显示「开始和该席位玩」后再选择《海盐折纸》。在桌面信息页检查已入座者、友谊赛模式与扩展开关，点「开始游戏」「进入游戏」。后台状态显示「本机热座牌桌已就绪」后，发送 `autoplay start`。助手核实全部参与者均由本机控制才会自动确认交接。命令 `inspect` 传 `牌局` 可查看当前桌号、座位和行动阶段。

BGA 正式站的 Hotseat 供 Premium 用户在同一设备轮流操作。在游戏页选双人桌，邀请玩家时选择 Hotseat 并输入第二席的称呼。游戏中按页面提示确认换座，Hotseat 不计官方 Elo。该席位的称呼用于本桌。后台指令清单可用于查看此时实际显示的按钮和提示。[BGA Hotseat 公告](https://en.boardgamearena.com/news?id=359) · [官方会员功能说明](https://forum.boardgamearena.com/viewtopic.php?t=14178) · [游戏页入口说明](https://forum.boardgamearena.com/viewtopic.php?p=210236) · [换座提示](https://forum.boardgamearena.com/viewtopic.php?t=31043)

## 出牌与统计口径

- 基础版策略根据自己手牌、已打出的牌和两堆可见明牌择牌。可凑对、形成已有收藏或取得美人鱼时倾向拿明牌，否则从牌库抽牌。优先打出可用组合。正常喊停时，只要“再给一次机会”（最后机会）可用就选它；只有该选项不可用且“立即结束”可用时才立即结算。
- 正式对局以 BGA 的最终名次为准。并列第一单列。同分时若 BGA 判为第一、第二，按该顺序记。独胜率为独占第一的局数除以收录的该玩家总局数。
- 每桌写入 `%LOCALAPPDATA%\SeaSaltDesk\matches\` 下的一份 JSON。以来源和桌号去重，Studio 和正式站分别统计。列表日期是本机收录时间。
- 页面等待对手时不出牌。遇到未知操作或点击后状态停滞，检查底部提示，手动处理后重新启动。扩展玩法暂不纳入自动策略。

## 从源码启动或发布

在此项目目录用 Windows PowerShell 执行，需要 .NET 10 SDK。

```powershell
dotnet run --project app/SeaSaltDesk.csproj
$publish = @('-c','Release','-r','win-x64','--self-contained','true','-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true')
dotnet publish app/SeaSaltDesk.csproj @publish -o dist/1.3.1-realtime
dotnet publish control/SeatControl.csproj @publish -o control/bin/publish
Copy-Item control/bin/publish/SeatControl.exe dist/1.3.1-realtime/SeatControl.exe
```

### 即时制 P0 候选包

在项目目录运行 `python tests/candidate.py build`。脚本冻结本次构建输入，生成带源码摘要的独立目录 `dist/candidates/<构建标识>/`，并输出 `candidate.json` 的 SHA-256。候选包的三个程序共同使用同一目录中的业务程序集，运行需要 .NET 10 Windows Desktop Runtime 和 WebView2。

复验人使用交接记录给出的目录和清单哈希运行 `python tests/candidate.py verify "<候选目录>" <清单SHA256>`。校验会启动桌面、控制器和检查程序的 `--build-identity` 入口，核对进程路径及实际加载程序集。校验成功后，从该目录的 `bin` 运行 `Checks.exe --clock-deadline`、`Checks.exe --realtime-three` 和 `Checks.exe --realtime-void`。受控服务端把接受的动作、行动方或阶段、牌桌及时间写入测试输出指定的账本。

低时限判断取活动己方座位的 `timeToThink_<id>` 实时显示。剩余 10 秒及以下，允许拿弃牌时优先取明牌，出牌阶段优先使用结束回合，结算阶段只点击已识别的确认按钮。BGA 对局客户端每秒更新席位时钟，并在 10 秒时发出提醒。[BGA 对局客户端](https://x.boardgamearena.net/data/themereleases/261028-1123/js/modules/layer/ly_studio.js)

运行中发现异常时，从候选包的 `bin` 执行 `SeatControl.exe --stop-queue <批次号>`。若启动时指定了 `--root`，此命令传相同目录。返回 0 表示已为该批次持久停止新入队。原有提交仍等平台确认，已分配桌继续出牌至结算。提前收尾的批次状态为 `stopped`，控制器退出码 4。重启同批次继续执行停排。隔离复验命令为 `Checks.exe --stop-queue-check`。

已向原桌重读但无法确认上一动作时，控制器自动触发同样的停排保护。用 `--stop-inflight-check` 验证已提交的匹配能收尾，用 `--stop-unresolved-check` 验证回执未明会停排且恢复后仍能打完当前桌。批次的 `<批次号>.ends.jsonl` 逐桌记录官方终局字段、页面操作次数和有服务端进展证据的动作数。正常完赛要求官方 `cancelled=0`、`endgame_reason=normal_end`、本次连续运行有己方动作后的服务端进展及完整排名。

渲染进程恢复用例由 `Checks.exe --recovery <故障>` 启动。支持 `crash`、`hang`、`urgent-hang`、`offline`、`unknown`、`long-offline`、`before-send`、`http-error`、`lost`。其中 `urgent-hang` 的独立时钟从七秒开始，分别检查旧进程退出和恢复后第二笔请求送达是否早于旗落。宿主在已读到己方时钟后可缩短无响应等待。首次读就卡住且没有可用时钟样本时，使用普通挂起阈值。断网期间无法送达的操作保持待证。

## 验证

```powershell
node --check app/table.js
node --check app/login.js
dotnet run --project tests/Checks.csproj
dotnet run --project tests/Checks.csproj -- --login-page
dotnet run --project tests/Checks.csproj -- --tutorial
dotnet run --project tests/Checks.csproj -- --online
```

离线检查使用受控网页和临时账号，覆盖从设置窗口保存凭据、下次启动尝试登录、登录成功和密码被拒绝，也覆盖 Cookie、验证码、账号或站点不符时的处理。牌桌场景从等待轮次、自动出牌运行到终局跳转和成绩入库。

后台命令进程另经本机 HTTPS 受控牌桌验证。通过管道启停出牌、读取日志和状态后，测试桌以 43∶37 入库一次。大厅页的列出控件、填写席位称呼、选择人数、点击和读取反馈也通过了检查。测试期间轮询前台窗口，未观察到后台进程取得焦点。

`--login-page` 使用独立的临时 WebView2 配置，只读检查两个官方站点匿名可见的用户名表单，不提交凭据。`--tutorial` 可单独复核中文官方教程的启停及结尾。`--online` 使用临时战绩库完成离线回归、公开结算桌和中文官方教程检查。[正式站登录页](https://en.boardgamearena.com/account) · [Studio 登录页](https://studio.boardgamearena.com/account) · [中文官方教学桌](https://zh-cn.boardgamearena.com/tutorial?game=seasaltpaper&tutorial=1027) · [公开结算样本](https://en.boardgamearena.com/table?table=879821206)

2026-11-04 在 Windows x64 上识别了正式站和 Studio 的匿名用户名表单。受控页通过了首次保存、启动登录与密码被拒绝的回归。中文官方教程走至结尾，未计入正式战绩。公开结算样本 41∶14 验证了玩家筛选及同桌去重。

同日复用玩家站已登录会话，通过后台命令开了一桌基础版双人友谊赛，第二席使用同设备热座。BGA 报告 7 轮、88 回合，最终两席比分为 45∶34。首步出现回执延迟，原桌重载后从服务器确认的选牌阶段继续。发布版已将含非链接热座昵称的官方结算完整入库，双方的玩家 ID、昵称、名次和分数与官方页面一致。再次打开同桌后提示战绩已存在，原记录逐字节不变。运行时可用 `log` 查看与右侧面板相同的中文记录。
