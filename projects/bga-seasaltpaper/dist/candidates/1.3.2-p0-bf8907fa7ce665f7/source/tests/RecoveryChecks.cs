using System.Text;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class RecoveryChecks
{
    // The fake server owns its state outside the renderer. Every HTTP(S) request is intercepted.
    // A stale document can send the same request twice; count it instead of silently de-duplicating.
    public static int Run(string fault)
    {
        ApplicationConfiguration.Initialize();
        string root = Directory.CreateTempSubdirectory("SeaSalt-recovery-").FullName;
        var url = new Uri("https://fixture.studio.boardgamearena.com/1/seasaltpaper?table=773");
        using var desk = new Desk(root, url, true);
        var web = desk.GameBrowser;
        var trace = new List<string>();
        var started = System.Diagnostics.Stopwatch.StartNew();
        void Log(string line)
        {
            line = $"{started.Elapsed.TotalSeconds:F2}s {line}";
            trace.Add(line); Console.WriteLine(line);
        }
        int loads = 0, accepted = 0, duplicates = 0, staleDocs = 0, dropped = 0, clicksAtFailure = 0;
        bool exited = false, heldOffline = false, verifiedOffline = false;
        int oldPid = 0, firstPostDocument = 0, unresolved = 0;
        long deadlineMs = 0, remainingMs = 0;
        var failure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        desk.LogLine += s => Log("desk " + s);
        desk.MatchedReceiptUnresolved += (table, intent) =>
        {
            if (table != "Studio/773/座位/10" || !Guid.TryParseExact(intent, "N", out _) || loads <= 1)
                throw new Exception("待确认事件缺少绑定席位或新文档。");
            unresolved++;
            Log($"incident receipt-unresolved-after-authoritative-read count={unresolved} document={loads}");
        };
        void Configure(TableBrowser view) => view.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = view.CoreWebView2;
            core.ProcessFailed += (_, e) =>
            {
                Log("process-failed " + e.ProcessFailedKind);
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.RenderProcessExited) exited = true;
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                string? body = null;
                if (uri == url)
                {
                    if (fault == "http-error" && loads == 1)
                    {
                        loads++;
                        Log("server GET table returned 503");
                        e.Response = core.Environment.CreateWebResourceResponse(null, 503, "Unavailable", "Cache-Control: no-store");
                        return;
                    }
                    if (fault == "urgent-hang" && loads == 0) deadlineMs = started.ElapsedMilliseconds + 7000;
                    int displayed = accepted;
                    if (fault == "unknown" && accepted == 1 && staleDocs++ < 2) displayed = 0;
                    loads++;
                    Log($"server GET table document={loads} shown={displayed} accepted={accepted}");
                    body = $$"""
                        <!doctype html><meta charset=utf-8>
                        <span id="move_nbr">{{displayed + 1}}</span><span id="connect_gs_status" hidden>连接中</span>
                        <div id="deck" style="width:50px;height:50px">抽牌</div><div id="discard1"></div><div id="discard2"></div>
                        <div class="player-table"><div data-my-hand="true" data-player-id="10"><div class="card" id="card-{{displayed}}" style="height:10px">牌</div></div></div>
                        <script>
                        window.bgaGameData={isRealTime:true};
                        window.gameui={player_id:10,table_id:773,game_name:"seasaltpaper",bRealtime:true,gamedatas:{
                            players:{10:{},20:{} },gamestate:{name:"takeCards",type:"activeplayer",active_player:10,args:{canTakeFromDeck:true,canTakeFromDiscard:[]} } },
                            isCurrentPlayerActive:()=>true};
                        if ("{{fault}}"==="urgent-hang") {
                            const clock=document.createElement("span");clock.id="timeToThink_10";document.body.append(clock);
                            const end=performance.now()+{{(deadlineMs-started.ElapsedMilliseconds)}};
                            const show=()=>clock.textContent="00:"+String(Math.max(0,Math.floor((end-performance.now())/1000))).padStart(2,"0");
                            show();setInterval(show,100);
                        }
                        if ("{{fault}}"==="before-send" && {{loads}}===1) {
                            const banner=document.getElementById("connect_gs_status");
                            let injected=false; const rects=banner.getClientRects.bind(banner);
                            banner.getClientRects=()=>{
                                if(!injected){const probe=new XMLHttpRequest();probe.open("GET","/intent-exists",false);probe.send();
                                    if(probe.responseText==="true"){injected=true;banner.hidden=false;setTimeout(()=>banner.hidden=true,1200);} }
                                return rects();
                            };
                        }
                        deck.onclick=()=>{
                            const req=new XMLHttpRequest();req.open("POST","/act?version={{displayed}}&document={{loads}}",false);req.send();
                            if({{displayed}}===0 && ["crash","hang","urgent-hang"].includes("{{fault}}")){while(true){} }
                        };
                        </script>
                        """;
                }
                if (fault == "before-send" && uri.AbsolutePath == "/intent-exists")
                    body = File.Exists(Path.Combine(root, "seat-actions", "Studio-773-10.json")) ? "true" : "false";
                if (fault == "lost" && uri.AbsolutePath == "/act")
                {
                    dropped++;
                    Log($"proxy dropped action before server; attempts={dropped} accepted={accepted}");
                    failure.TrySetResult();
                }
                else if (uri.Host == url.Host && uri.AbsolutePath == "/act")
                {
                    var f = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    remainingMs = deadlineMs - started.ElapsedMilliseconds;
                    if (heldOffline || f["version"] != accepted.ToString() || fault == "urgent-hang" && remainingMs <= 0) duplicates++;
                    else accepted++;
                    Log($"server POST version={f["version"]} document={f["document"]} accepted={accepted} duplicates={duplicates}");
                    if (fault == "urgent-hang") Log($"server deadline remaining={remainingMs}ms");
                    body = "{}";
                    if (accepted == 1) { firstPostDocument = int.Parse(f["document"]!); failure.TrySetResult(); }
                }
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body ?? "")),
                    body == null ? 404 : 200, body == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        };
        Configure(web);
        desk.BrowserCreated += view => { Log("single-controller replacement"); Configure(view); };
        Exception? error = null;
        desk.Shown += async (_, _) =>
        {
            try
            {
                async Task Until(Func<bool> predicate, int seconds)
                {
                    var end = DateTime.UtcNow.AddSeconds(seconds);
                    while (!predicate())
                    {
                        if (DateTime.UtcNow >= end) throw new TimeoutException("未观察到服务端后续合法操作。");
                        await Task.Delay(100);
                    }
                }
                Task? supervisor = null;
                if (fault == "long-offline")
                {
                    var book = new SeriesBook(root);
                    var batch = book.Start(1, "10", "", Guid.NewGuid().ToString("N"), "Studio");
                    batch = book.Queue(batch.Id, true);
                    supervisor = RealtimeSeries.Play(desk, book, batch, url, (_, _) => {});
                    await Until(()=>desk.IsPlaying || supervisor.IsCompleted, 12);
                    if (supervisor.IsCompleted) await supervisor;
                }
                else await Until(()=>desk.StartMatchedSeat("Studio", "773", "10", "20"), 12);
                await failure.Task.WaitAsync(TimeSpan.FromSeconds(8));
                var core = web.CoreWebView2;
                clicksAtFailure = desk.MatchedActions;
                string journal = Path.Combine(root, "seat-actions", "Studio-773-10.json");
                using var firstIntent = System.Text.Json.JsonDocument.Parse(File.ReadAllText(journal));
                var firstPlan = firstIntent.RootElement.GetProperty("plan").GetRawText();
                oldPid = (int)core.Environment.GetProcessInfos().Single(p=>p.Kind == CoreWebView2ProcessKind.Renderer).ProcessId;
                Log($"fault {fault} renderer={oldPid} table=773 self=10 opponent=20");
                if (fault == "crash")
                {
                    // PID 只来自刚为此用例新建的临时 profile，且该环境只有这一个 renderer。
                    if (Path.GetFullPath(core.Environment.UserDataFolder) != Path.GetFullPath(Path.Combine(root, "profile")))
                        throw new Exception("故障注入目标不是本用例的临时资料目录。");
                    using var owned = System.Diagnostics.Process.GetProcessById(oldPid);
                    if (owned.ProcessName != "msedgewebview2") throw new Exception("故障注入 PID 身份不符。");
                    owned.Kill();
                }
                // hang 模式直接在点击处理器内死循环，宿主永远收不到本次提交的返回。
                else if (fault is "offline" or "long-offline")
                {
                    Task SetOffline(bool value) => core.CallDevToolsProtocolMethodAsync("Network.emulateNetworkConditions",
                        $$"""{"offline":{{(value ? "true" : "false")}},"latency":0,"downloadThroughput":-1,"uploadThroughput":-1}""");
                    await core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                    await SetOffline(true); heldOffline = true;
                    verifiedOffline = await core.ExecuteScriptAsync("navigator.onLine") == "false";
                    if (fault == "long-offline") await core.ExecuteScriptAsync("delete gameui.gamedatas.players");
                    int duration = fault == "long-offline" ? 125000 : 5500;
                    await Task.Delay(duration);
                    if (supervisor?.IsCompleted == true) await supervisor;
                    if (!desk.IsPlaying || accepted != 1) throw new Exception("长断网期间失去席位或离线重复操作。");
                    Log($"network offline={verifiedOffline} duration={duration}ms accepted={accepted} supervisor-active={supervisor?.IsCompleted == false}");
                    heldOffline = false; await SetOffline(false);
                }
                if (fault == "lost")
                {
                    await Until(()=>loads >= 3, 18);
                    if (!desk.IsPlaying || dropped != 1 || accepted != 0 || !File.Exists(journal) || unresolved != 1)
                        throw new Exception("未抵达服务器的请求在结果不明时被误重试或解除席位。");
                    Log($"held unresolved: accepted=0 dispatched-once={dropped} fresh-documents={loads}, retained-seat=True");
                    return;
                }
                await Until(()=>accepted == 2 || duplicates != 0, fault == "hang" ? 55 : 26);
                // 将第一代已落盘的派发凭据强行送给恢复后的真实适配器，应在点击前拒绝。
                using var stream = typeof(TableBrowser).Assembly.GetManifestResourceStream("SeaSaltDesk.table.js")!;
                using var reader = new StreamReader(stream);
                var stale = await desk.GameBrowser.CoreWebView2.ExecuteScriptAsync(
                    $"({reader.ReadToEnd()})('Studio/773/座位/10',null,'20',{{commit:{firstPlan},expires:Date.now()+3000}})");
                using var staleReply = System.Text.Json.JsonDocument.Parse(stale);
                if (staleReply.RootElement.GetProperty("kind").GetString() != "not-sent")
                    throw new Exception("旧文档派发凭据在新控制器没有被拒绝。");
                Log($"stale grant rejected pre-click; local reported clicks at fault={clicksAtFailure}");
                bool oldGone = !desk.GameBrowser.CoreWebView2.Environment.GetProcessInfos().Any(p=>p.ProcessId == oldPid);
                Log($"done accepted={accepted} duplicates={duplicates} loads={loads} old-pid-gone={oldGone} exited={exited} playing={desk.IsPlaying}");
                if (accepted != 2 || duplicates != 0 || loads < 2 || !desk.IsPlaying || unresolved != (fault == "unknown" ? 1 : 0) ||
                    fault is "crash" or "hang" or "urgent-hang" && (!oldGone || clicksAtFailure != 0) ||
                    fault == "urgent-hang" && remainingMs <= 0 || fault == "http-error" && loads < 3 ||
                    fault == "crash" && !exited || fault is "offline" or "long-offline" && !verifiedOffline ||
                    fault == "unknown" && staleDocs < 3 || fault == "before-send" && firstPostDocument < 2)
                    throw new Exception("同阶段回执、代际或服务端行动断言失败。");
            }
            catch (Exception ex) { error = ex; Log($"FAIL {ex.GetType().Name} {ex.Message} accepted={accepted} duplicates={duplicates}"); }
            finally { File.WriteAllLines(Path.Combine(root,"trace.txt"), trace); desk.Close(); }
        };
        Application.Run(desk);
        Console.WriteLine("测试证据 " + Path.Combine(root, "trace.txt"));
        return error == null ? 0 : 1;
    }
}
