extern alias SeatWorker;
using SeaSaltDesk;

internal static class Checks
{
    private static void Require(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--resume-seat", var resumeCase]) return ResumeSeatChecks.Run(resumeCase);
        if (args is ["--turn-receipt", var receipt]) return TurnReceiptChecks.Run(receipt);
        if (args is ["--turn-receipt", "reopen", var seed, var ledger]) return TurnReceiptChecks.Run("reopen", seed, ledger);
        if (args is ["--bound-end", var proof]) return RealtimeChecks.Run(voidFirst: proof == "cancel", endProof: proof);
        if (args is ["--fallback", var fallback]) return FallbackChecks.Run(fallback);
        if (args is ["--allocation", var allocation]) return AllocationChecks.Run(allocation);
        if (args is ["--lock-clock", var mode]) return LockClockChecks.Run(mode);
        if (args is ["--build-identity"]) { BuildIdentity.Write(typeof(SeatWorker::SeriesRunner).Assembly); return 0; }
        try { return args is ["--penalty-seat", var outcome] ? PenaltySeatChecks.Run(outcome) :
            args is ["--penalty-preflight"] ? PenaltyChecks.Run() :
            args is ["--penalty-inflight", var phase] ? PenaltyChecks.Run(phase) :
            args is ["--recovery", var fault] ? RecoveryChecks.Run(fault) :
            args.Contains("--background-check") ? BackgroundChecks.Run() :
            args.Contains("--clock-deadline") ? ClockDeadlineChecks.Run() :
            args.Contains("--stop-queue-check") ? RealtimeChecks.Run(stopAt: "draw") :
            args.Contains("--stop-inflight-check") ? RealtimeChecks.Run(stopAt: "join") :
            args.Contains("--stop-unresolved-check") ? RealtimeChecks.Run(stopAt: "unresolved") :
            args.Contains("--realtime-void") ? RealtimeChecks.Run(true) :
            args.Contains("--unknown-end-check") ? RealtimeChecks.Run(unknownEnd: true) :
            args.Contains("--realtime-three") ? RealtimeChecks.Run() : Run(args); }
        catch (Exception ex) { return ReportFailure(ex); }
    }

    private static int ReportFailure(Exception ex)
    {
        // 仅展示本套检查主动给出的中文断言。系统组件的报错可能使用其他语言。
        bool fromCheck = ex.TargetSite?.Module.Assembly == typeof(Checks).Assembly &&
            (ex.GetType() == typeof(Exception) || ex is TimeoutException) && !string.IsNullOrWhiteSpace(ex.Message);
        string reason = fromCheck ? ex.Message : ex switch
        {
            TimeoutException => "页面或控件未在规定时间内就绪。",
            IOException or UnauthorizedAccessException => "无法读写测试资料，请检查临时目录。",
            InvalidOperationException => "未找到预期的页面控件或测试数据。",
            _ => $"测试遇到系统错误，错误码为 0x{ex.HResult:X8}。"
        };
        Console.Error.WriteLine("检查失败：" + reason);
        return 1;
    }

    private static int Run(string[] args)
    {
        string temp = Directory.CreateTempSubdirectory("SeaSaltDesk-check-").FullName;
        SeriesBookChecks.Run(temp);
        var book = new MatchBook(Path.Combine(temp, "matches"));
        Seat a = new("10", "甲", 1, 40), b = new("20", "乙", 2, 40);
        Match match = new("BGA", "123", [a, b]);
        Require(book.Add(match) && !book.Add(match), "同站同桌去重失败。");
        Require(book.Add(match with { Table = "124", Players = [a, b with { Rank = 1 }] }), "并列第一未入库。");
        Require(book.Add(match with { Site = "Studio" }), "不同站点的同桌号未分别入库。");
        var saved = new MatchBook(Path.Combine(temp, "matches")).Load();
        Require(MatchBook.ForPlayer(saved.Select(x => x.Match), "BGA", a.Id) == new Summary(2, 1, 1, 40), "玩家甲的战绩汇总不符。");
        Require(MatchBook.ForPlayer(saved.Select(x => x.Match), "BGA", b.Id) == new Summary(2, 0, 1, 40), "同分时未按官方排名统计。");
        Require(TableBrowser.Address("123").Query == "?table=123" && !TableBrowser.IsBga(new("https://notboardgamearena.com/")), "桌号或站点识别失败。");
        Console.WriteLine("通过 · 战绩存储、去重、并列第一、同分排名与站点识别。");

        ApplicationConfiguration.Initialize();
        string scenario = Path.Combine(temp, "scenario");
        using var form = new Desk(scenario, TableScenario.GameUrl) { ShowInTaskbar = false, Opacity = 0 };
        var controls = Children(form).ToList();
        var view = controls.OfType<TableBrowser>().Single();
        TableScenario.Serve(view);
        int exitCode = 1;
        form.Shown += async (_, _) =>
        {
            try
            {
                if (args.Contains("--tutorial") && !args.Contains("--online"))
                {
                    await OnlineDesk(temp, tutorialOnly: true);
                    exitCode = 0;
                    return;
                }
                var toggle = controls.OfType<Button>().Single(b => b.Text == "启动自动出牌");
                var status = controls.OfType<Label>().Single(l => l.Name == "status");
                await TableScenario.Run(view, toggle, status, new MatchBook(Path.Combine(scenario, "matches")));
                await StatsChecks.Run(view, Path.Combine(temp, "stats-check"));
                await HotseatChecks.Run(view);
                await TurnBasedSwitchChecks.Run(view);
                await LoginScenario.Run(temp);
                if (args.Contains("--login-page")) await LoginScenario.CheckPublicPages(temp);
                if (args.Contains("--online")) await OnlineDesk(temp);
                exitCode = 0;
            }
            catch (Exception ex) { exitCode = ReportFailure(ex); }
            finally { form.Close(); }
        };
        Application.Run(form);
        // WebView2 can keep its isolated profile open briefly after the last window closes.
        for (int attempt = 0; attempt < 12; attempt++)
        {
            try { Directory.Delete(temp, recursive: true); break; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
        if (Directory.Exists(temp)) Console.Error.WriteLine("测试临时目录仍在使用：" + temp);
        return exitCode;
    }

    internal static IEnumerable<Control> Children(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));

    private static async Task OnlineDesk(string temp, bool tutorialOnly = false)
    {
        string isolated = Path.Combine(temp, "desktop");
        using var desk = new Desk(isolated, new(tutorialOnly ? TableBrowser.Tutorial : "https://en.boardgamearena.com/table?table=879821206"))
            { ShowInTaskbar = false, Opacity = 0 };
        desk.Show();
        var controls = Children(desk).ToList();
        var tabs = controls.OfType<TabControl>().Single();
        var status = controls.OfType<Label>().Single(l => l.Name == "status");
        var web = controls.OfType<TableBrowser>().Single();
        Button Button(string initial) => controls.OfType<Button>().Single(b => b.Text == initial);
        async Task WaitFor(Func<bool> check, int seconds = 120)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            string last = "";
            while (!check())
            {
                if (last != status.Text) { Console.WriteLine(status.Text); last = status.Text; }
                if (DateTime.UtcNow > until) throw new TimeoutException(status.Text);
                await Task.Delay(350);
            }
        }
        var liveBook = new MatchBook(Path.Combine(isolated, "matches"));
        if (!tutorialOnly)
        {
            await WaitFor(() => status.Text.Contains("战绩已保存"));
            var game = liveBook.Load().Single().Match;
            Require(game.Table == "879821206" && game.Players.Length == 2 &&
                game.Players.Any(p => p.Rank == 1 && p.Score == 41) && game.Players.Any(p => p.Rank == 2 && p.Score == 14), "公开结算桌的名次或得分不符。");
            tabs.SelectedIndex = 1;
            var filter = controls.OfType<ComboBox>().Single();
            filter.SelectedIndex = 1;
            Require(controls.OfType<DataGridView>().Single().Rows.Count == 1 &&
                Children(tabs.SelectedTab!).OfType<Label>().Any(l => l.Text.Contains("1 局") && l.Text.Contains("独胜率")), "玩家筛选或统计展示不符。");
            tabs.SelectedIndex = 0;
            Button("打开").PerformClick();
            await WaitFor(() => status.Text.Contains("战绩已存在"));
            Require(liveBook.Load().Count == 1, "再次打开同桌后出现重复战绩。");
            Console.WriteLine("通过 · 在线公开结算桌：41∶14 入库、玩家筛选、重复打开去重。");
            Button("单人教程").PerformClick();
        }
        var toggle = Button("启动自动出牌");
        await WaitFor(() => toggle.Enabled && status.Text.Contains("教学桌已就绪"));
        await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('#didomi-notice-disagree-button')?.click()");
        toggle.PerformClick();
        Require(toggle.Text.Contains("暂停"), "启动后按钮未切换为暂停。");
        toggle.PerformClick();
        Require(toggle.Text.Contains("启动"), "暂停后按钮未切换为启动。");
        await Task.Delay(1000);
        toggle.PerformClick();
        await WaitFor(() => !toggle.Enabled && (status.Text.Contains("教程结尾") || status.Text.Contains("已完成单人教程")), 400);
        Require(liveBook.Load().Count == (tutorialOnly ? 0 : 1), "单人教程意外写入了正式战绩。");
        Console.WriteLine("通过 · 中文官方教程：启动、暂停、恢复、抵达结尾，战绩库无新增记录。");
    }
}
