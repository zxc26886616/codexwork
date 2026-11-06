using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class PenaltySeatChecks
{
    public static int Run(string outcome)
    {
        if (outcome is not ("restore" or "blocked" or "revoked" or "interleaved")) throw new ArgumentException("未知处罚恢复场景。");
        bool remainsBlocked = outcome == "blocked", revoked = outcome is "revoked" or "interleaved";
        bool interleaved = outcome == "interleaved";
        ApplicationConfiguration.Initialize();
        string root = Directory.CreateTempSubdirectory("SeaSalt-penalty-seat-").FullName;
        var game = new Uri("https://fixture.studio.boardgamearena.com/1/seasaltpaper?table=773");
        using var desk = new Desk(root, game, true);
        var web = desk.GameBrowser;
        var book = new SeriesBook(root);
        var batch = book.Start(3, "10", "", Guid.NewGuid().ToString("N"), "Studio");
        batch = book.Queue(batch.Id, true);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lines = new List<string>();
        void Log(string text) { text = $"{clock.Elapsed.TotalSeconds:F2}s {text}"; lines.Add(text); Console.WriteLine(text); }
        int accepted = 0, duplicate = 0, penalties = 0, tableReads = 0, penaltyClicks = 0;
        bool oldReadHeld = false, releasedAfterNavigation = false;
        bool boundAtFirst = false, intentAtFirst = false, stoppedBeforeProbe = false, intentBeforeProbe = false;
        string? firstIntent = null;
        string journal = Path.Combine(root, "seat-actions", "Studio-773-10.json");
        desk.LogLine += s => Log("desk " + s);
        web.CoreWebView2InitializationCompleted += (_, init) =>
        {
            if (!init.IsSuccess) return;
            var core = web.CoreWebView2;
            CoreWebView2WebResourceRequestedEventArgs? heldRequest = null;
            CoreWebView2Deferral? heldDeferral = null;
            core.NavigationStarting += (_, e) => {
                var target=new Uri(e.Uri);
                Log($"navigation-start path={target.AbsolutePath} bound={desk.IsPlaying}");
                if (target.AbsolutePath=="/other" && heldRequest!=null && heldDeferral!=null) {
                    heldRequest.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes("{}")),200,"OK","Content-Type:application/json");
                    releasedAfterNavigation=true;
                    Log("release old game observer after NavigationStarting");
                    heldDeferral.Complete();heldDeferral=null;heldRequest=null;
                }
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                string? body = null;
                if (uri == game)
                {
                    tableReads++;
                    if (tableReads > 1)
                    {
                        stoppedBeforeProbe = book.QueueStopped(batch.Id);
                        intentBeforeProbe = File.Exists(journal) && File.ReadAllText(journal) == firstIntent;
                    }
                    Log($"server table GET number={tableReads} accepted={accepted} stopped={book.QueueStopped(batch.Id)}");
                    if (remainsBlocked && accepted > 0)
                        body = """<!doctype html><script>location.assign("/penalty?id=PRIVATE_FIXTURE")</script>""";
                    else body = $$"""
                        <!doctype html><meta charset=utf-8><span id="move_nbr">{{accepted + 1}}</span>
                        <div id="deck" style="width:60px;height:60px">抽牌</div><div id="discard1"></div><div id="discard2"></div>
                        <div class="player-table"><div data-my-hand="true" data-player-id="10">
                        <div class="card" id="held-{{accepted}}" style="height:10px">牌</div></div></div>
                        <script>
                        globalUserInfos={id:"10"};bgaGameData={isRealTime:true};
                        if ("{{outcome}}"==="interleaved") Object.defineProperty(globalUserInfos,"id",{get(){
                            if(window.afterFirst&&!window.observerHeld){
                                window.observerHeld=true;
                                const held=new XMLHttpRequest();held.open("GET","/hold-old-observer",false);
                                try{held.send();}catch{}
                            }
                            return "10";
                        } });
                        gameui={player_id:10,table_id:773,game_name:"seasaltpaper",bRealtime:true,gamedatas:{
                            players:{10:{},20:{} },gamestate:{name:"takeCards",type:"activeplayer",active_player:10,
                            args:{canTakeFromDeck:true,canTakeFromDiscard:[]} } },isCurrentPlayerActive:()=>true};
                        deck.onclick=()=>{const req=new XMLHttpRequest();req.open("POST","/action?version={{accepted}}",false);req.send();
                            if({{accepted}}===0){
                                if("{{outcome}}"==="interleaved")window.afterFirst=true;
                                else location.assign("/{{(revoked ? "other" : "penalty")}}?id=PRIVATE_FIXTURE&n=PRIVATE_REASON#PRIVATE_FRAGMENT");
                            } };
                        </script>
                        """;
                }
                else if (uri.Host == game.Host && uri.AbsolutePath == "/penalty")
                {
                    penalties++;
                    Log($"server penalty GET bound={desk.IsPlaying} pending={File.Exists(journal)}");
                    body = """<!doctype html><h1>处罚提醒</h1><button id="play_now" onclick="fetch('/penalty-action',{method:'POST'})">明白了</button>""";
                }
                else if (uri.Host == game.Host && uri.AbsolutePath == "/hold-old-observer") {
                    oldReadHeld=true;heldRequest=e;heldDeferral=e.GetDeferral();
                    Log("hold old game observer; host requests /other");
                    core.Navigate(new Uri(game,"/other").AbsoluteUri);
                    return;
                }
                else if (uri.Host == game.Host && uri.AbsolutePath == "/other") {
                    body = """<!doctype html><script>location.assign("/penalty?id=PRIVATE_FIXTURE")</script>""";
                    if (interleaved) {
                        var deferral=e.GetDeferral();string response=body;
                        async Task CompleteOther() {
                            // 保留旧文档到观察脚本返回，固定导航已开始、旧game回执随后到达的交错。
                            await Task.Delay(400);
                            e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(response)),200,"OK","Content-Type:text/html");
                            Log("deliver /other document after old observer release");
                            deferral.Complete();
                        }
                        _=CompleteOther();return;
                    }
                }
                else if (uri.Host == game.Host && uri.AbsolutePath == "/penalty-action") { penaltyClicks++; body = ""; }
                else if (uri.Host == game.Host && uri.AbsolutePath == "/action")
                {
                    var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    if (e.Request.Method != "POST" || q["version"] != accepted.ToString()) duplicate++;
                    else accepted++;
                    if (accepted == 1)
                    {
                        boundAtFirst = desk.IsRunningMatchedSeat(book.Load(batch.Id));
                        intentAtFirst = File.Exists(journal);
                        if (intentAtFirst) firstIntent = File.ReadAllText(journal);
                    }
                    Log($"server POST accepted={accepted} duplicate={duplicate} boundAtFirst={boundAtFirst} intentAtFirst={intentAtFirst}");
                    body = "{}";
                }
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body ?? "")),
                    body == null ? 404 : 200, body == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control:no-store");
            };
        };
        int exit = 1;
        desk.Shown += async (_, _) =>
        {
            Task? supervisor = null;
            try
            {
                supervisor = RealtimeSeries.Play(desk, book, batch, game, (_, message) => Log("series " + message));
                var until = DateTime.UtcNow.AddSeconds(10);
                while (penalties == 0 && DateTime.UtcNow < until) await Task.Delay(50);
                if (penalties == 0) throw new Exception("第一手后没有进入处罚提醒页。");
                await Task.Delay(900);
                if (revoked)
                {
                    string incidentPath=Path.ChangeExtension(book.PathFor(batch.Id), ".incidents.jsonl");
                    Log($"original-900ms task={supervisor.Status} stopped={book.QueueStopped(batch.Id)} incidents={File.Exists(incidentPath)}");
                    var deadline=DateTime.UtcNow.AddSeconds(5);
                    while (!supervisor.IsCompleted && DateTime.UtcNow<deadline &&
                        (!File.Exists(incidentPath) || !File.ReadAllText(incidentPath).Contains("platform-penalty-seat-revoked")))
                        await Task.Delay(50);
                    // 真实故障必须直接失败并保留原异常；不能用等待或缺文件异常掩盖。
                    if(supervisor.IsFaulted) await supervisor;
                    var state = book.Load(batch.Id);
                    string incidents = File.Exists(incidentPath)?File.ReadAllText(incidentPath):"";
                    bool pending = File.Exists(journal) && File.ReadAllText(journal) == firstIntent;
                    if (supervisor.IsCompleted || desk.IsPlaying || !boundAtFirst || !intentAtFirst || !pending ||
                        !book.QueueStopped(batch.Id) || state.CurrentTable != "773" || state.OpponentId != "20" ||
                        !incidents.Contains("platform-penalty-seat-revoked") || !lines.Any(s => s.Contains("控制器已失权")) ||
                        accepted != 1 || duplicate != 0 || penaltyClicks != 0 ||
                        interleaved && (!oldReadHeld || !releasedAfterNavigation))
                        throw new Exception("控制器失权未明确报告，或错误提交后续动作。");
                    Log("PASS revoked controller explicitly reported; association and original intent retained; no second action.");
                    exit = 0;
                    return;
                }
                if (!desk.IsPlaying || !boundAtFirst || !intentAtFirst)
                    throw new Exception($"实际出牌后跳转撤销绑定 bound={desk.IsPlaying}, first={boundAtFirst}, pendingAtFirst={intentAtFirst}。");
                while ((remainsBlocked ? tableReads < 2 || penalties < 2 : accepted < 2) && DateTime.UtcNow < until)
                    await Task.Delay(50);
                var saved = book.Load(batch.Id);
                if (supervisor.IsCompleted) await supervisor;
                bool pendingRetained = File.Exists(journal) && File.ReadAllText(journal) == firstIntent;
                bool ok = stoppedBeforeProbe && intentBeforeProbe && !supervisor.IsCompleted &&
                    desk.IsRunningMatchedSeat(saved) && ReferenceEquals(web, desk.GameBrowser) &&
                    book.QueueStopped(batch.Id) && saved.CurrentTable == "773" && saved.OpponentId == "20" &&
                    saved.Completed == 0 && duplicate == 0 && penaltyClicks == 0 &&
                    (remainsBlocked ? accepted == 1 && penalties >= 2 && pendingRetained : accepted == 2 && desk.MatchedConfirmedActions >= 1);
                Log($"result ok={ok} accepted={accepted} duplicate={duplicate} penaltyClicks={penaltyClicks} tableReads={tableReads} penalties={penalties} bound={desk.IsPlaying} pendingRetained={pendingRetained} confirmed={desk.MatchedConfirmedActions} stopped={book.QueueStopped(batch.Id)}");
                if (!ok || lines.Any(s=>s.Contains("PRIVATE_"))) throw new Exception("处罚跳转后的原桌恢复或保留在途意图断言失败。");
                Log(remainsBlocked ? "PASS cannot restore: retained binding and intent; stopped new queue; no duplicate or penalty click."
                    : "PASS restored same table after accepted first move; second server accept; pending action verified; no duplicate or penalty click.");
                exit = 0;
            }
            catch (Exception ex) { Log("FAIL " + ex.Message); }
            finally
            {
                if (supervisor != null) _ = supervisor.ContinueWith(t=>_ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                File.WriteAllLines(Path.Combine(root, "evidence.txt"), lines);
                desk.Close();
            }
        };
        Application.Run(desk);
        Console.WriteLine("受控证据 " + Path.Combine(root, "evidence.txt"));
        return exit;
    }
}
