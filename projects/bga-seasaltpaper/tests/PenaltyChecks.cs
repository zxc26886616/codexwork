using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class PenaltyChecks
{
    public static int Run(string phase = "preflight")
    {
        ApplicationConfiguration.Initialize();
        string root = Directory.CreateTempSubdirectory("SeaSalt-penalty-check-").FullName;
        var lobby = new Uri("https://fixture.studio.boardgamearena.com/lobby");
        var trace = new List<string>();
        void Log(string text) { trace.Add(text); Console.WriteLine(text); }
        if (phase is not ("preflight" or "pending" or "pending-lock" or "offer" or "table")) throw new ArgumentException("未知测试阶段。");
        bool outstanding = phase != "preflight";
        var penalty = new Uri(lobby, "/penalty?id=PRIVATE_FIXTURE_QUERY&n=PRIVATE_FIXTURE_REASON#PRIVATE_FIXTURE_FRAGMENT");
        using var desk = new Desk(root, outstanding ? penalty : lobby, true);
        var web = desk.GameBrowser;
        int joins = 0, penaltyClicks = 0, navigation = 0;
        web.CoreWebView2InitializationCompleted += (_, init) =>
        {
            if (!init.IsSuccess) return;
            var core = web.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var url = new Uri(e.Request.Uri);
                string? html = null;
                if (url.Host == lobby.Host && url.AbsolutePath == "/lobby")
                    html = """
                        <!doctype html><meta charset=utf-8><script>globalUserInfos={id:"10"};</script>
                        <div id="gamelobby-module" class="rankingmode_simple gamemode_realtime opponentmode_automatic">
                        <div id="Xannaplay_1631"><button id="Xannaplay_pref_1631"
                        onclick="location.assign('/penalty?id=PRIVATE_FIXTURE_QUERY&n=PRIVATE_FIXTURE_REASON#PRIVATE_FIXTURE_FRAGMENT')">匹配偏好</button></div></div>
                        """;
                else if (url.Host == lobby.Host && url.AbsolutePath == "/penalty")
                {
                    navigation++;
                    html = """
                        <!doctype html><meta charset=utf-8><script>globalUserInfos={id:"10"};</script>
                        <h1>平台处罚页受控替身</h1>
                        <button id="play_now" onclick="fetch('/penalty-action',{method:'POST'})">确认</button>
                        """;
                }
                else if (url.Host == lobby.Host && url.AbsolutePath == "/table")
                    html = """<!doctype html><script>location.assign("/penalty?id=PRIVATE_FIXTURE_QUERY")</script>""";
                else if (url.Host == lobby.Host && url.AbsolutePath == "/penalty-action") { penaltyClicks++; html = ""; }
                else if (url.Host == lobby.Host && url.AbsolutePath == "/join") { joins++; html = ""; }
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(html ?? "")),
                    html == null ? 404 : 200, html == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
            };
        };
        int exit = 1;
        desk.Shown += async (_, _) =>
        {
            var book = new SeriesBook(root);
            var batch = book.Start(3, "10", "", Guid.NewGuid().ToString("N"), "Studio");
            if (outstanding) batch = book.Queue(batch.Id, true);
            if (phase == "offer") batch = book.AcceptOffer(batch.Id, "773", "20");
            if (phase == "table") batch = book.Attach(batch.Id, "773", "20");
            Task? playing = null, lockHolder = null;
            var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                var initialized = DateTime.UtcNow.AddSeconds(6);
                while ((web.CoreWebView2 == null || web.Source?.Scheme == "about") && DateTime.UtcNow < initialized) await Task.Delay(50);
                if (phase == "pending-lock")
                {
                    var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    lockHolder = Task.Run(() =>
                    {
                        using var gate = new Mutex(false, @"Local\SeaSaltDesk.Queue." + batch.Id);
                        gate.WaitOne();
                        try { acquired.SetResult(); releaseGate.Task.GetAwaiter().GetResult(); }
                        finally { gate.ReleaseMutex(); }
                    });
                    await acquired.Task;
                }
                playing = RealtimeSeries.Play(desk, book, batch, lobby, (_, message) => Log("series " + message));
                var until = DateTime.UtcNow.AddSeconds(8);
                while (web.Source?.AbsolutePath != "/penalty" && DateTime.UtcNow < until) await Task.Delay(50);
                if (web.Source?.AbsolutePath != "/penalty") throw new Exception("没有复现从匹配偏好跳到处罚页。");
                await Task.Delay(150);
                string read = await LobbyControls.Run(web, "realtime", "");
                Log("read " + read);
                var completion = await Task.WhenAny(playing, Task.Delay(1800));
                bool rejected = false;
                if (completion == playing)
                {
                    try { await playing; }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("处罚") && ex.Message.Contains("/penalty"); Log("exit " + ex.Message); }
                }
                var saved = book.Load(batch.Id);
                Log($"state rejected={rejected} joins={joins} penaltyClicks={penaltyClicks} navigation={navigation} queuePending={saved.QueuePending} current={saved.CurrentTable ?? "null"} offer={saved.OfferTable ?? "null"} completed={saved.Completed}");
                using var reply = JsonDocument.Parse(read);
                var value = reply.RootElement;
                bool stopped = phase == "pending-lock" ? !book.QueueStopped(batch.Id) &&
                    trace.Any(s => s.Contains("本进程已停止新匹配")) : book.QueueStopped(batch.Id);
                bool expectedExit = outstanding ? !playing.IsCompleted && !rejected && stopped &&
                    ReferenceEquals(web, desk.GameBrowser) && !web.IsDisposed : rejected;
                if (!expectedExit || !value.TryGetProperty("kind", out var kind) || kind.GetString() != "blocked" ||
                    value.GetProperty("code").GetString() != "platform-penalty" ||
                    value.GetProperty("origin").GetString() != lobby.GetLeftPart(UriPartial.Authority) ||
                    value.GetProperty("path").GetString() != "/penalty" ||
                    read.Contains("PRIVATE_FIXTURE") || trace.Any(s=>s.Contains("PRIVATE_FIXTURE")) ||
                    joins != 0 || penaltyClicks != 0 || navigation < 1 || saved.QueuePending != batch.QueuePending ||
                    saved.CurrentTable != batch.CurrentTable || saved.OfferTable != batch.OfferTable ||
                    saved.OpponentId != batch.OpponentId || saved.Completed != 0)
                    throw new Exception("处罚前置页未快速阻断，或诊断包含查询信息/发生队列副作用。");
                var click = await LobbyControls.Run(web, "realtime-click", "mode|10|||play_now");
                if (penaltyClicks != 0 || !click.Contains("platform-penalty")) throw new Exception("处罚页仍允许匹配点击。");
                await web.CoreWebView2!.ExecuteScriptAsync("history.replaceState({},'', '/penalty-extra?id=PRIVATE_FIXTURE_QUERY')");
                var other = await LobbyControls.Run(web, "realtime-click", "mode|10|||play_now");
                if (other.Contains("platform-penalty") || penaltyClicks != 0 || other.Contains("PRIVATE_FIXTURE"))
                    throw new Exception("处罚分类不是精确路径，或扩大了控件允许范围。");
                Log(outstanding
                    ? $"PASS {phase} retained, same WebView retained, supervisor alive, new queue held; no joins or penalty clicks."
                    : "PASS exact penalty blocked; zero joins, offers, seats, completions and penalty clicks; query and fragment redacted.");
                exit = 0;
            }
            catch (Exception ex) { Log("FAIL " + ex.Message); }
            finally
            {
                releaseGate.TrySetResult();
                if (lockHolder != null) await lockHolder;
                if (playing != null) _ = playing.ContinueWith(t=>_ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                File.WriteAllLines(Path.Combine(root, "evidence.txt"), trace);
                desk.Close();
            }
        };
        Application.Run(desk);
        Console.WriteLine("受控证据 " + Path.Combine(root, "evidence.txt"));
        return exit;
    }
}
