using System.Text;
using System.Text.Json;
using SeaSaltDesk;

internal sealed class SeriesRunner
{
    private const string Origin = "https://boardgamearena.com";
    private static readonly Uri Lobby = new(Origin + "/lobby");
    private readonly Settings options;
    private readonly SeriesBook book;
    private readonly string folder, stateFile, intentFile;
    private SeriesState? run;
    private DateTime lastReport, lastProgress;
    private string lastMessage = "";
    private bool retryStatusReplace;
    private string? lastPage;
    private StreamWriter? log;

    private sealed record Settings(string Root, string Id, int Count, string Guest, bool Resume, string? RecoverTable,
        string? RealtimeSite, bool TurnBased);
    private SeriesRunner(Settings settings)
    {
        options = settings;
        book = new(settings.Root);
        folder = Path.GetDirectoryName(book.PathFor(settings.Id))!;
        stateFile = Path.Combine(folder, settings.Id + ".status.json");
        intentFile = Path.Combine(folder, settings.Id + ".creating");
    }

    private static Settings Parse(string[] args)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaSaltDesk");
        string? id = null, recover = null;
        string guest = "FoldTest", site = "Studio";
        int count = 0;
        bool resume = false;
        var keys = new HashSet<string>();
        for (int i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !keys.Add(args[i])) throw new ArgumentException();
            string value = args[i + 1];
            switch (args[i])
            {
                case "--series" or "--realtime" when int.TryParse(value, out count) && count is >= 1 and <= 1000: break;
                case "--site" when value is "BGA" or "Studio": site = value; break;
                case "--speed" when value == "turnbased": break;
                case "--root": root = Path.GetFullPath(value); break;
                case "--guest": guest = value.Trim(); break;
                case "--batch-id": id = value; break;
                case "--resume": resume = true; id = value; break;
                case "--recover-table" when IsId(value): recover = value; break;
                default: throw new ArgumentException();
            }
        }
        bool realtime = keys.Contains("--realtime");
        if ((resume ? 1 : 0) + (realtime ? 1 : 0) + (keys.Contains("--series") ? 1 : 0) != 1 ||
            (keys.Contains("--site") && !realtime) || (realtime && (keys.Contains("--guest") || keys.Contains("--speed"))) ||
            resume && (keys.Contains("--guest") || keys.Contains("--batch-id")) ||
            recover != null && !resume || !SeriesBook.ValidGuestName(guest)) throw new ArgumentException();
        id ??= Guid.NewGuid().ToString("N");
        if (!Guid.TryParseExact(id, "N", out var guid)) throw new ArgumentException();
        return new(root, guid.ToString("N"), count, realtime ? "" : guest, resume, recover, realtime ? site : null,
            keys.Contains("--speed"));
    }

    private static bool IsId(string id) => id.Length is > 0 and <= 18 && id[0] != '0' && id.All(char.IsAsciiDigit);

    public static int MainSeries(string[] args, Action<Desk>? configureDesk = null)
    {
        Settings options;
        try { options = Parse(args); }
        catch (ArgumentException) { return 2; }
        var runner = new SeriesRunner(options);
        try
        {
            Directory.CreateDirectory(runner.folder);
            try
            {
                // 同批次永远只有一个写入方。回合制单独占用验证槽，让即时制开发可同时运行。
                using var batchLease = new FileStream(Path.Combine(runner.folder, options.Id + ".run.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
                bool turnBased = options.TurnBased || (File.Exists(runner.book.PathFor(options.Id)) &&
                    runner.book.Load(options.Id).TurnBasedFrom.HasValue);
                using var modeLease = File.Open(Path.Combine(options.Root, turnBased ? "turnbased-run.lock" : "series-run.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return runner.RunDesktop(configureDesk);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return 3; }
        }
        catch (Exception) { return 1; }
    }

    private int RunDesktop(Action<Desk>? configureDesk)
    {
        int code = 1;
        try
        {
            using var writer = new StreamWriter(Path.Combine(folder, options.Id + ".log"), true, new UTF8Encoding(false)) { AutoFlush = true };
            log = writer;
            if (options.Resume || File.Exists(book.PathFor(options.Id))) run = book.Load(options.Id);
            if (run != null && !options.Resume && (run.Planned != options.Count || run.GuestName != options.Guest ||
                run.Realtime != (options.RealtimeSite != null) || run.Site != (options.RealtimeSite ?? "BGA")))
                throw new InvalidDataException("批次编号已用于其他设置。");
            if (run?.Finished == true) { Report("completed", "该批次已经完成。", true); return 0; }
            if (run is { Realtime:true, CurrentTable:null, OfferTable:null, QueuePending:false } && book.QueueStopped(run.Id))
            {
                Report("stopped", "本批次已停止新入队。", true);
                return 4;
            }
            if (options.TurnBased && run?.Realtime == true)
                throw new InvalidOperationException("真人即时批次不能作为回合制热座批次恢复。");
            if (File.Exists(intentFile) && run?.CurrentTable == null && options.RecoverTable == null)
                throw new InvalidDataException("上次建桌请求的结果尚未确认。请核对大厅后，用 --resume 和 --recover-table 指定已经创建的桌号。");
            if (options.RecoverTable != null && (!File.Exists(intentFile) || run?.CurrentTable != null))
                throw new InvalidDataException("只有建桌结果未确认的批次可以指定待核实桌号。");
            string? realtimeSite = run?.Realtime == true ? run.Site : options.RealtimeSite;
            var lobby = realtimeSite == "Studio" ? new Uri("https://studio.boardgamearena.com/lobby") : Lobby;
            if (realtimeSite != null) WriteLog($"即时制构建　座位 {typeof(Desk).Assembly.ManifestModule.ModuleVersionId:N}，批次 {typeof(SeriesRunner).Assembly.ManifestModule.ModuleVersionId:N}。");
            Report("running", "正在连接游戏大厅。", true);
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var desk = new Desk(options.Root, lobby, background: true);
            configureDesk?.Invoke(desk);
            // 显式恢复只守在途桌；即使这一桌结束也不能开启下一次匹配。
            bool stopAfterCurrent = options.Resume && run?.Realtime == true;
            if (stopAfterCurrent) {
                try { book.StopQueue(run!.Id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) {
                    Console.Error.WriteLine("恢复进程已停止续排，暂未完整写入停排标记。");
                }
            }
            bool fallbackActive = false;
            void TryFallbackReport(string status, string message)
            {
                try { Report(status, message, true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                    Console.Error.WriteLine("原桌救场状态暂未写入，本进程保留原桌并停止续排。");
                }
            }
            bool StartFallback(Exception error)
            {
                if (realtimeSite == null || options.Resume || error is InvalidDataException ||
                    error is not (TimeoutException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or JsonException)) return false;
                SeriesState saved;
                try { saved = book.Load(options.Id); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
                if (run == null || !saved.Realtime || saved.Finished || saved.HostId != run.HostId || saved.Site != run.Site ||
                    saved.OpponentId == null || (saved.CurrentTable ?? saved.OfferTable) == null ||
                    (run.CurrentTable ?? run.OfferTable) is { } knownTable && knownTable != (saved.CurrentTable ?? saved.OfferTable) ||
                    run.OpponentId != null && run.OpponentId != saved.OpponentId) return false;
                // 先建立内存保护，再写可能失败的日志/status。没有第二条 Play，也不依赖异常报告成功。
                stopAfterCurrent = true;
                fallbackActive = true;
                run = saved;
                bool started = desk.BeginMatchedFallback(saved, lobby, state => {
                    var current = book.Load(state.Id);
                    if (current.HostId != state.HostId || current.Site != state.Site || current.OpponentId != state.OpponentId ||
                        (current.CurrentTable ?? current.OfferTable) != (state.CurrentTable ?? state.OfferTable))
                        throw new InvalidDataException("救场原桌绑定已变化。");
                    return run = book.Attach(state.Id, (state.CurrentTable ?? state.OfferTable)!, state.OpponentId);
                }, notice => {
                    Evidence(notice.Code, notice);
                    if (notice.Code is "supervisor-fallback-ended" or "supervisor-fallback-rejected") {
                        TryFallbackReport("degraded", notice.Code == "supervisor-fallback-ended" ?
                            "原桌救场已取得官方终局，保留事故与原关联，不计正常完赛。" :
                            "原桌救场授权已撤销，保留原关联及意图，已停止续排。");
                        code = notice.Code == "supervisor-fallback-ended" ? 4 : 1;
                        desk.Close();
                    } else TryFallbackReport("degraded", "原桌已由独立出牌器接席，救场不计正常验收。");
                });
                if (!started) { fallbackActive = false; return false; }
                Evidence("supervisor-fallback-started", new {
                    exception = error.GetType().FullName, hresult = $"0x{error.HResult:X8}", operation = error.Data["operation"],
                    browser = System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(desk.GameBrowser),
                    armed = desk.IsPlaying, actions = desk.MatchedActions });
                if (error is PrearmObservationTimeoutException) desk.RevokeUnarmedFallbackRead();
                TryFallbackReport("degraded", "匹配监督器发生技术异常，独立核对原桌，已停止续排；救场不计正常验收。");
                return true;

                void Evidence(string eventCode, object details)
                {
                    // 停排、事故和观测相互独立；任何一项写失败都不能撤销已建立的内存保护。
                    try { book.StopQueue(saved.Id); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) { }
                    book.RecordIncident(run!, eventCode, desk.MatchedActions);
                    try { File.AppendAllText(Path.ChangeExtension(book.PathFor(saved.Id), ".fallback.jsonl"),
                        JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, code = eventCode, degraded = true,
                            batch = saved.Id, site = saved.Site, table = saved.CurrentTable ?? saved.OfferTable,
                            self = saved.HostId, opponent = saved.OpponentId, processId = Environment.ProcessId, details }) + Environment.NewLine); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            desk.MatchedReceiptUnresolved += (seatKey, intent) =>
            {
                if (run is not { Realtime:true, CurrentTable:not null } ||
                    seatKey != $"{run.Site}/{run.CurrentTable}/座位/{run.HostId}") return;
                // 即使磁盘暂时无法写入，也先拦住本进程下一次入队；当前牌桌继续核实同一笔操作。
                bool scoreOnly = desk.UnresolvedScoreIntent == intent;
                if (!scoreOnly) stopAfterCurrent = true;
                try
                {
                    if (scoreOnly) book.HoldScoreReceipt(run);
                    else book.StopQueue(run.Id);
                    if (!book.RecordIncident(run, scoreOnly ? "score-ack-receipt-unresolved" :
                        "receipt-unresolved-after-authoritative-read", desk.MatchedActions))
                    {
                        stopAfterCurrent = true;
                        book.StopQueue(run.Id);
                    }
                    WriteLog($"桌号 {run.CurrentTable} 原桌重读后回执未明，保留席位，已停止续排。操作 {intent}。");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                {
                    stopAfterCurrent = true;
                    try { book.StopQueue(run.Id); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or TimeoutException) { }
                    Console.Error.WriteLine("当前进程已停止续排，未能完整持久化停排记录。");
                }
            };
            desk.LogLine += message => {
                try { WriteLog(message); }
                catch (Exception e) when (fallbackActive && e is IOException or UnauthorizedAccessException) { }
            };
            desk.Shown += async (_, _) =>
            {
                try
                {
                    if (realtimeSite != null)
                    {
                        var targetSite = realtimeSite == "Studio" ? LoginSite.Studio : LoginSite.BGA;
                        var expectedUser = new LoginVault(options.Root).Read(targetSite)?.User;
                        string self = await Account(desk.GameBrowser, realtimeSite, desk, lobby, expectedUser);
                        run ??= book.Start(options.Count, self, "", options.Id, realtimeSite);
                        if (self != run.HostId) throw new InvalidOperationException("登录账号与即时制批次不符。");
                        await RealtimeSeries.Play(desk, book, run, lobby, (state, message) =>
                        {
                            run = state;
                            Report(message == desk.DrawHandoffMessage ? "needs-takeover" : "running", message);
                        }, () => stopAfterCurrent, guardedPrearm: !options.Resume, recoverCurrentSeat: options.Resume);
                    }
                    else await Play(desk);
                    if (run is { Finished:false, Realtime:true } && (stopAfterCurrent || book.QueueStopped(run.Id)))
                    {
                        Report("stopped", "本批次已停止新入队，在途牌桌已核对完毕。", true);
                        code = 4;
                    }
                    else { Report("completed", "已达到设定局数，全部战绩已收录。", true); code = 0; }
                }
                catch (Exception ex)
                {
                    if (StartFallback(ex)) return;
                    string reason = Reason(ex);
                    string? table = run?.CurrentTable ?? run?.OfferTable;
                    if (ex is VoidedRealtimeMatchException)
                    {
                        Report("failed", reason, true);
                        desk.Close();
                        return;
                    }
                    if (realtimeSite != null && table != null && run != null &&
                        !book.RecordIncident(run, "series-error-in-table", desk.MatchedActions))
                        desk.ShowMatchedTable("原桌异常无法写入磁盘，请检查本机日志目录");
                    if (realtimeSite != null && table != null && configureDesk == null)
                    {
                        if (run != null && desk.IsRunningMatchedSeat(run) &&
                            ex is TimeoutException or IOException or System.Runtime.InteropServices.COMException)
                        {
                            // 监督器故障不撤销独立运行且仍绑定本人的座位；不再匹配下一桌。
                            Report("failed", reason + "　原桌继续自动出牌，已停止后续匹配。", true);
                            desk.ShowMatchedTable("本桌继续自动出牌，已停止后续匹配");
                            return;
                        }
                        Report("failed", reason + "　已显示原桌，可立即接管。", true);
                        desk.HandOverMatchedTable(new Uri(lobby, "/tableview?table=" + table), reason);
                        return;
                    }
                    Report("failed", reason, true);
                }
                desk.Close();
            };
            Application.Run(desk);
            return code;
        }
        catch (Exception ex)
        {
            log = null;
            Report("failed", Reason(ex), true);
            return 1;
        }
    }

    private static string Reason(Exception ex) => ex switch
    {
        InvalidDataException or InvalidOperationException or TimeoutException or VoidedRealtimeMatchException
            when ex.Message.Any(c => c is >= '一' and <= '龥') => ex.Message,
        FileNotFoundException => "没有找到指定的批次文件。",
        IOException or UnauthorizedAccessException => "无法读写本机批次或战绩文件。",
        _ => $"批次中断，错误码为 0x{ex.HResult:X8}。"
    };

    private void WriteLog(string message)
    {
        lastProgress = DateTime.UtcNow;
        var planned = run?.Planned ?? options.Count;
        log?.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss}　已完成 {run?.Completed ?? 0}/{planned}　{run?.CurrentTable ?? "大厅"}　{message}");
    }

    private void Report(string status, string message, bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && !retryStatusReplace && lastMessage == message && now - lastReport < TimeSpan.FromSeconds(12)) return;
        if (force || message != lastMessage) WriteLog(message);
        lastMessage = message;
        lastReport = now;
        var data = new { batchId = options.Id, status, message, table = run?.CurrentTable, completed = run?.Completed ?? 0,
            planned = run?.Planned ?? options.Count, processId = Environment.ProcessId, updated = DateTimeOffset.Now };
        string temp = stateFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(data, MatchBook.Json));
            try { File.Move(temp, stateFile, true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                e.Data["operation"] = "replace-status-snapshot";
                // 只延后原桌的一次普通进度快照。下一轮用最新状态重试，不阻塞出牌线程，也不重放动作。
                if (retryStatusReplace || force || status != "running" || run is not { Realtime: true, CurrentTable: not null } ||
                    e.HResult is not (unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021))) throw;
                retryStatusReplace = true;
                WriteLog("当前桌进度快照暂时无法替换，下次报告重试。");
                return;
            }
            retryStatusReplace = false;
        }
        finally { File.Delete(temp); }
    }

    private void BeforeCreate()
    {
        if (run == null || run.Finished || run.CurrentTable != null || File.Exists(intentFile))
            throw new InvalidOperationException("上一桌尚未核实，已阻止再次建桌。");
        using var intent = new FileStream(intentFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        intent.Write(Encoding.UTF8.GetBytes(DateTimeOffset.Now.ToString("O")));
        intent.Flush(true);
        Report("running", "已请求创建新的双人本机热座，等待 BGA 分配桌号。", true);
    }

    private static async Task<string> Account(TableBrowser web, string site = "BGA", Desk? signinDesk = null, Uri? lobby = null, string? expectedUser = null)
    {
        bool attempted = false;
        var until = DateTime.UtcNow.AddSeconds(120);
        while (DateTime.UtcNow < until)
        {
            if (web.CoreWebView2 != null && web.Source is { } u && TableBrowser.IsBga(u))
            {
                if (u.AbsolutePath == "/account")
                {
                    if (signinDesk == null || lobby == null || attempted)
                        throw new InvalidOperationException("所选站点尚未登录，请检查自动登录设置。");
                    attempted = true;
                    await signinDesk.SignInForSeriesAsync(site == "Studio" ? LoginSite.Studio : LoginSite.BGA);
                    await Go(signinDesk, lobby);
                    continue;
                }
                var json = await web.CoreWebView2.ExecuteScriptAsync($$"""
                    (()=>{const studio = /(^|\.)studio\.boardgamearena\.com$/.test(location.hostname);
                    const player = /^(?:(?:en|zh-cn|www)\.)?boardgamearena\.com$/.test(location.hostname);
                    if (!({{(site == "Studio" ? "studio" : "player")}})) return "";
                    const p = window.globalUserInfos, expected = {{JsonSerializer.Serialize(expectedUser)}};
                    if (expected && Number(p?.id) > 0 && String(p.name).toLowerCase() !== expected.toLowerCase()) return "mismatch";
                    return String(p?.id || "");})()
                    """);
                string id = JsonSerializer.Deserialize<string>(json) ?? "";
                if (id == "mismatch") throw new InvalidOperationException("当前登录账号与本机所选站点的测试账号不符。");
                if (IsId(id)) return id;
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("未能在所选站点确认登录账号。");
    }

    private static async Task Go(Desk desk, Uri target)
    {
        var core = desk.GameBrowser.CoreWebView2;
        var done = new TaskCompletionSource();
        void Loaded(object? _, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult();
        core.NavigationCompleted += Loaded;
        try { desk.OpenPage(target); await done.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { core.NavigationCompleted -= Loaded; }
    }

    private async Task Play(Desk desk)
    {
        var web = desk.GameBrowser;
        string host = await Account(web);
        run ??= book.Start(options.Count, host, options.Guest, options.Id);
        if (run.HostId != host) throw new InvalidOperationException("当前玩家与批次房主不符。");
        if (options.TurnBased && run.TurnBasedFrom == null) run = book.UseTurnBased(run.Id);
        bool turnBased = run.TurnBasedFrom.HasValue;
        string? converting = turnBased ? run.CurrentTable : null;
        var lobby = new HotseatLobby(BeforeCreate, turnBased, converting);
        var resumeTable = run.CurrentTable ?? options.RecoverTable;
        // 与大厅建桌链接一致。普通桌号链接在开局前可能转到游戏面板的匹配入口。
        if (resumeTable != null) await Go(desk, new Uri(Origin + "/table?table=" + resumeTable + "&nr=true"));
        else if (web.Source?.AbsolutePath != "/lobby") await Go(desk, Lobby);
        bool armed = false;
        lastProgress = DateTime.UtcNow;
        while (!run.Finished)
        {
            await Task.Delay(750);
            if (DateTime.UtcNow - lastProgress > TimeSpan.FromMinutes(4))
                throw new TimeoutException("牌桌连续四分钟没有进展，已保留当前桌号，可按批次恢复。");
            var u = web.Source;
            if (u == null || !TableBrowser.IsBga(u)) continue;
            if (u.AbsolutePath == "/account") throw new InvalidOperationException("登录会话已失效，请重新登录后恢复本批次。");
            string? number = System.Web.HttpUtility.ParseQueryString(u.Query)["table"];
            string page = u.AbsolutePath switch
            {
                "/lobby" => "大厅",
                "/table" => "牌桌信息",
                "/gamepanel" => "游戏面板",
                "/tableview" => "游戏桌",
                var path when path.EndsWith("/seasaltpaper", StringComparison.Ordinal) => "海盐折纸游戏桌",
                _ => "其他页面"
            };
            if (number != null && IsId(number)) page += " · 桌号 " + number;
            if (page != lastPage) { lastPage = page; WriteLog("当前页面为" + page + "。"); }
            if (number != null && run.CurrentTable != null && number != run.CurrentTable)
                throw new InvalidOperationException("页面已离开本批当前桌，已停止操作。");

            if (run.CurrentTable is { } table && u.AbsolutePath == "/table" && number == table)
            {
                var next = book.Collect(run.Id, table);
                if (next.Completed > run.Completed)
                {
                    run = next;
                    File.Delete(intentFile);
                    var round = run.Rounds[^1];
                    Report("running", $"第 {run.Completed} 局已结算，桌号 {table}。" +
                        $"{round.Host.Name} 第 {round.Host.Rank} 名、{round.Host.Score} 分，" +
                        $"{round.Guest.Name} 第 {round.Guest.Rank} 名、{round.Guest.Score} 分。", true);
                    if (run.Finished) return;
                    armed = false;
                    lobby = new HotseatLobby(BeforeCreate, turnBased);
                    await Go(desk, Lobby);
                    continue;
                }
            }

            if (u.AbsolutePath == "/tableview" || u.AbsolutePath.EndsWith("/seasaltpaper", StringComparison.Ordinal))
            {
                if (number == null || run.CurrentTable != number)
                    throw new InvalidOperationException("进行中的牌桌未绑定到本批次，已停止操作。");
                if (turnBased && !armed)
                {
                    bool? official = await HotseatLobby.ReadTurnBased(web);
                    if (official == false)
                    {
                        if (number != converting) throw new InvalidOperationException("BGA 新桌并非回合制，已停止出牌。");
                        converting = null;
                        Report("running", $"正在通过 BGA 为原桌 {number} 提议转为回合制。", true);
                        if (!await TurnBasedSwitch.TryConvert(web, number, host, run.GuestName))
                            throw new InvalidOperationException("BGA 尚未确认原桌已转为回合制。已保留桌号与战绩。");
                        continue;
                    }
                    if (official == null) { Report("running", "正在等待 BGA 确认本桌为回合制。"); continue; }
                }
                if (!desk.IsPlaying && !armed && desk.StartTwoLocalSeats(number, host))
                {
                    armed = true;
                    Report("running", $"第 {run.Completed + 1} 局已启动{(turnBased ? "官方回合制" : "")}双人热座自动出牌。", true);
                }
                else if (armed && !desk.IsPlaying && desk.PauseReason is { } reason)
                    throw new InvalidOperationException("自动出牌已停止。" + reason);
                else Report("running", $"第 {run.Completed + 1} 局进行中，桌号 {number}。");
            }
            else
            {
                var step = await lobby.Tick(web, run.GuestName);
                if (step.HostId != null && step.HostId != host) throw new InvalidOperationException("牌桌房主与批次不符。");
                if (step.State == "blocked") throw new InvalidOperationException(step.Message);
                if (step.Table is { } created && run.CurrentTable == null)
                {
                    if (step.State != "created" || step.HostId != host || !File.Exists(intentFile) ||
                        !IsId(created) || options.RecoverTable != null && resumeTable != null && created != resumeTable ||
                        run.Rounds.Any(r => r.Table == created))
                        throw new InvalidOperationException("不能把旧桌或未请求的牌桌加入本批次。");
                    run = book.Attach(run.Id, created);
                    File.Delete(intentFile);
                    resumeTable = null;
                }
                Report("running", step.Message);
            }
        }
    }
}
