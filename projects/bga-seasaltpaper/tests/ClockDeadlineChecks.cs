using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class ClockDeadlineChecks
{
    private static readonly Uri Game = new("https://clock.studio.boardgamearena.com/1/seasaltpaper?table=788");
    private const string Page = """
        <!doctype html><meta charset=utf-8>
        <div id="deck">牌库</div><div id="connect_gs_status" hidden>离线</div>
        <span id="timeToThink_10">1:20</span><span id="timeToThink_20">0:01</span>
        <div id="discard2" hidden><div id="shell" class="card" data-category="3" data-family="1">贝壳</div></div>
        <div data-my-hand="true" data-player-id="10">
          <div id="crab" class="card bga-cards_selectable-card" data-category="2" data-family="1">螃蟹</div>
        </div>
        <div id="generalactions"><button disabled id="playCards_button">Play selected cards</button>
          <button id="end">End turn</button><button id="cancel_button">Cancel</button></div>
        <script>
        const by = id => document.getElementById(id);
        window.state = {id:30,name:"playCards",type:"activeplayer",active_player:"10",possibleactions:["actEndTurn"]};
        window.bgaGameData={isRealTime:true};
        window.gameui = {player_id:"10",game_name:"seasaltpaper",table_id:"788",
          gamedatas:{players:{"10":{},"20":{}},gamestate:state},last_server_state:structuredClone(state),
          isCurrentPlayerActive:()=>state.active_player==="10",isInterfaceLocked:()=>window.locked===true};
        window.selections=0;
        by("crab").onclick=()=>window.selections++;
        by("shell").onclick=()=>window.tookDiscard=true;
        by("cancel_button").onclick=()=>fetch("/wrong");
        by("end").onclick=async()=>{
            window.locked=true;
            const receipt=await(await fetch("/action?table=788&seat=10&phase=playCards&name=actEndTurn",{method:"POST"})).json();
            if(receipt.accepted){
                state.name=receipt.next;state.active_player=receipt.active;
                gameui.last_server_state=structuredClone(state);window.receiptApplied=true;
            }
            window.locked=false;
        };
        </script>
        """;

    public static int Run()
    {
        string root = Directory.CreateTempSubdirectory("SeaSalt-clock-").FullName;
        string evidence = Path.Combine(root, "clock.jsonl");
        Console.WriteLine("棋钟证据 " + evidence);
        void Record(object row) => File.AppendAllText(evidence, JsonSerializer.Serialize(row) + Environment.NewLine);
        ApplicationConfiguration.Initialize();
        using var desk = new Desk(root, Game, background: true);
        var browser = desk.GameBrowser;
        var timer = new Stopwatch();
        const int budgetMs = 4000;
        int received = 0, wrong = 0;
        var failedGuards = new List<string>();
        long remainingMs = 0;
        string serverTurn = "10", serverPhase = "playCards";
        browser.CoreWebView2InitializationCompleted += (_, init) =>
        {
            if (!init.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var url = new Uri(e.Request.Uri);
                string content = ""; int code = 404;
                if (url == Game) { content = Page; code = 200; }
                if (url.Host == Game.Host && url.AbsolutePath == "/action")
                {
                    var q = System.Web.HttpUtility.ParseQueryString(url.Query);
                    received++;
                    remainingMs = budgetMs - timer.ElapsedMilliseconds;
                    bool accepted = timer.IsRunning && remainingMs > 0 && e.Request.Method == "POST" &&
                        q["table"] == "788" && q["seat"] == serverTurn && q["phase"] == serverPhase && q["name"] == "actEndTurn";
                    if (accepted) { serverTurn = "20"; serverPhase = "takeCards"; }
                    Record(new { source = "fixture-server", action = q["name"], table = q["table"], seat = q["seat"],
                        elapsedMs = timer.ElapsedMilliseconds, remainingMs, accepted, nextActive = serverTurn, nextPhase = serverPhase });
                    content = JsonSerializer.Serialize(new { accepted, active = serverTurn, next = serverPhase }); code = 200;
                }
                if (url.Host == Game.Host && url.AbsolutePath == "/wrong") wrong++;
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)), code,
                    code == 200 ? "OK" : "Not found", url == Game ? "Content-Type: text/html; charset=utf-8" : "Content-Type: application/json");
            };
        };
        int exit = 1;
        desk.Shown += async (_, _) =>
        {
            try
            {
                var status = Checks.Children(desk).OfType<Label>().Single(l => l.Name == "status");
                var until = DateTime.UtcNow.AddSeconds(25);
                while (!status.Text.Contains("牌桌已就绪") && DateTime.UtcNow < until) await Task.Delay(80);
                if (!status.Text.Contains("牌桌已就绪")) throw new Exception("未打开隔离牌桌。");
                var core = browser.CoreWebView2;
                const string key = "Studio/788/座位/10";
                async Task Probe(string condition, string setup, string? pending = null, string other = "20")
                {
                    await core.ExecuteScriptAsync(setup);
                    var read = await browser.ReadAsync(key, pending, other);
                    Record(new { source = "negative-probe", condition, read.Kind, read.Message, received });
                    if (read.Kind == "action" || received != 0) failedGuards.Add(condition);
                }
                // 对手在一秒内到期，己方 80 秒。照常选择本地配对牌，不能触发低时限结束。
                var normal = await browser.ReadAsync(key, null, "20");
                if (normal.Kind != "action" || await core.ExecuteScriptAsync("selections") != "1" || received != 0)
                    throw new Exception("使用了对手的低棋钟或未走常规选牌。");
                Record(new { source = "opponent-clock-control", ownSeconds = 80, otherSeconds = 1, normal.Kind, normal.Message, received });
                await core.ExecuteScriptAsync("by('timeToThink_10').textContent='0:04';");
                await Probe("unknown-realtime-string", "delete bgaGameData.isRealTime;gameui.bRealtime='0';");
                await Probe("mode-conflict", "bgaGameData.isRealTime=true;gameui.bRealtime=false;");
                await Probe("opponent-turn", "delete gameui.bRealtime;state.active_player='20'");
                await Probe("disconnected", "state.active_player='10';by('connect_gs_status').hidden=false;");
                await Probe("animation-lock", "by('connect_gs_status').hidden=true;window.locked=true;");
                await Probe("pending-same-phase", "window.locked=false;", "10/playCards");
                await Probe("wrong-opponent", "void 0", other:"99");
                await Probe("unknown-actions", "state.name='futureState';by('crab').hidden=true;by('end').hidden=true;");
                await core.ExecuteScriptAsync("""
                    state.name='takeCards';state.args={canTakeFromDeck:true,canTakeFromDiscard:['2']};
                    by('discard2').hidden=false;by('timeToThink_10').textContent='0:10';
                    """);
                var shortDraw = await browser.ReadAsync(key, null, "20");
                if (shortDraw.Kind != "action" || await core.ExecuteScriptAsync("window.tookDiscard===true") != "true")
                    throw new Exception("临界十秒未识别字符串弃牌堆许可，仍要求从牌库抽牌。");
                Record(new { source = "short-draw-control", ownSeconds = 10, possiblePile = "2", shortDraw.Kind, shortDraw.Message });
                await core.ExecuteScriptAsync("by('discard2').hidden=true;delete state.args;");
                await core.ExecuteScriptAsync("state.name='playCards';state.active_player='20';by('crab').hidden=false;by('end').hidden=false;");
                int priorSelections = int.Parse(await core.ExecuteScriptAsync("selections"));
                if (!desk.StartMatchedSeat("Studio","788","10","20")) throw new Exception("未能绑定受控桌。");
                timer.Start();
                Record(new { source = "fixture-server", turn = serverTurn, phase = serverPhase, budgetMs });
                await core.ExecuteScriptAsync("""
                    state.active_player="10"; gameui.last_server_state=structuredClone(state);
                    const start=performance.now(); const tick=()=>by("timeToThink_10").textContent=
                        "0:"+String(Math.max(0,Math.ceil((4000-(performance.now()-start))/1000))).padStart(2,"0");
                    tick(); setInterval(tick,200);
                    """);
                while (timer.ElapsedMilliseconds < budgetMs && await core.ExecuteScriptAsync("window.receiptApplied===true") != "true")
                    await Task.Delay(60);
                int selections = int.Parse(await core.ExecuteScriptAsync("selections"));
                bool applied = await core.ExecuteScriptAsync("window.receiptApplied===true") == "true";
                Record(new { source = "deadline-result", received, remainingMs, serverTurn, serverPhase,
                    priorSelections, selections, wrong, applied, failedGuards });
                if (received != 1 || remainingMs <= 0 || serverTurn != "20" || serverPhase != "takeCards" ||
                    selections != priorSelections || wrong != 0 || !applied || failedGuards.Count > 0)
                    throw new Exception("低时限结束没有得到服务端的唯一按时受理与行动方推进。");
                await Task.Delay(1100);
                if (received != 1 || !desk.IsPlaying) throw new Exception("回执后的受控桌重复提交或出牌器已退出。");
                Record(new { source = "verified", received, remainingMs, serverTurn, serverPhase, selections, wrong });
                Console.WriteLine($"通过 · 受控服务器在余 {remainingMs} 毫秒确认结束回合，行动方 10 → 20。");
                exit = 0;
            }
            catch (Exception e) { Console.Error.WriteLine(e.Message); }
            finally { desk.Close(); }
        };
        Application.Run(desk);
        return exit;
    }
}
