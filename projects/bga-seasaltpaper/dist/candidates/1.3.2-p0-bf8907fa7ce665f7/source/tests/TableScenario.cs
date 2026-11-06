using SeaSaltDesk;

internal static class TableScenario
{
    // Synthetic site in this isolated WebView only. No calls go to a BGA game server.
    private const string Host = "fixture.studio.boardgamearena.com";
    public static readonly Uri GameUrl = new("https://" + Host + "/1/seasaltpaper?table=77");
    private const string ResultPage = """
        <!doctype html><meta charset="utf-8"><main class="tablewithgame_seasaltpaper"><div id="game_result">
        <div id="score_entry_10"><span class="rank">1st</span><a class="playername">甲</a><span class="score">43</span></div>
        <div id="score_entry_20"><span class="rank">2nd</span><a class="playername">乙</a><span class="score">37</span></div>
        </div></main>
        """;
    internal const string Page = """
        <!doctype html><meta charset="utf-8"><style>.card{display:inline-block;width:30px;height:35px}</style>
        <div id="deck"></div><div id="discard1"></div><div id="discard2"></div>
        <div id="pick"></div><div id="discard-pick"></div>
        <div class="player-table"><div id="mine" data-my-hand="true" data-player-id="10"></div><div class="table cards"></div></div>
        <div id="generalactions"></div>
        <script>
        const node = id => document.getElementById(id);
        const state = {name:"takeCards", args:{canTakeFromDeck:true,canTakeFromDiscard:[1,2]}};
        window.ourTurn = false;
        window.gameui = {gamedatas:{gamestate:state}, isCurrentPlayerActive:()=>window.ourTurn};
        const put = (parent,id,type,family,fn) => {
            const e=document.createElement("div"); e.className="card bga-cards_selectable-card";e.id=id;e.dataset.side="front";
            e.innerHTML='<div class="front" data-category="'+type+'" data-family="'+family+'" data-color="2"></div>';
            e.onclick=fn;node(parent).append(e);return e;
        };
        const button=(id,name,fn) => {const b=document.createElement("button"); b.id=id;b.textContent=name;
            b.onclick=fn;node("generalactions").append(b);return b;};
        function choosePair(e){e.currentTarget.classList.add("bga-cards_selected-card");
            if(document.querySelectorAll("#mine .bga-cards_selected-card").length===2)node("playCards_button").disabled=false;}
        let round=1;
        function finish(){state.name="gameEnd";node("deck").remove();}
        function nextRound(){
            state.name="endRound";state.type="game";
            node("generalactions").replaceChildren();node("discard-pick").replaceChildren();
            setTimeout(()=>{
                round=2;state.type="activeplayer";state.name="takeCards";
                node("discard2").replaceChildren();
                put("mine","swimmer",2,4,choosePair);
                put("discard2","shark",2,5,e=>{
                    node("mine").append(e.currentTarget);
                    e.currentTarget.onclick=choosePair;actionPhase();
                });
            },2300);
        }
        function actionPhase(){
            state.name="playCards";
            const play=button("playCards_button","Play selected cards",()=>{
                node("mine").replaceChildren();node("generalactions").replaceChildren();
                if(round===1){state.name="chooseDiscardPile";return;}
                state.name="chooseOpponent";
                button("choosePlayer20-button","乙",()=>{
                    node("generalactions").replaceChildren();state.name="playCards";
                    button("immediateEndRound_button","STOP",finish);
                });});
            play.disabled=true;
        }
        put("mine","crab-one",2,1,choosePair);
        function revealPick(){state.name="chooseCard";
            put("pick","shell",3,1,()=>state.name="wrong");
            put("pick","crab-two",2,1,e=>{node("mine").append(e.currentTarget);
                e.currentTarget.onclick=choosePair;node("pick").replaceChildren();state.name="putDiscardPile";});}
        put("deck","deck-top",0,0,()=>{
            // 模拟服务器已记下抽牌，但此页没有收到状态切换通知。加快本测试的锁超时。
            sessionStorage.awaitingPick="1";
            window.gameui.isInterfaceLocked=()=>true;
            const clock=Date.now;
            setTimeout(()=>{Date.now=()=>clock()+31000;},1900);
        });
        if(sessionStorage.awaitingPick==="1"){
            delete sessionStorage.awaitingPick;window.ourTurn=true;revealPick();
        }
        put("discard1","shell-b",3,1,null);put("discard2","sailor-b",3,4,null);
        node("discard1").onclick=()=>{if(state.name==="putDiscardPile")actionPhase();
            else if(state.name==="chooseDiscardPile"){state.name="chooseDiscardCard";put("discard-pick","mermaid",1,1,()=>{
                state.name="playCards";button("immediateEndRound_button","STOP",()=>{state.name="beforeEndRound";
                    node("generalactions").replaceChildren();button("seen_button","Seen",nextRound);});});}};
        </script>
        """;

    public static void Serve(TableBrowser browser)
    {
        browser.CoreWebView2InitializationCompleted += (_, initialized) =>
        {
            if (!initialized.IsSuccess) return;
            var core = browser.CoreWebView2;
            core.AddWebResourceRequestedFilter("https://" + Host + "/*",
                Microsoft.Web.WebView2.Core.CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri = new Uri(e.Request.Uri);
                if (uri.Host != Host) return;
                string? html = uri == GameUrl ? Page : uri.PathAndQuery == "/table?table=77" ? ResultPage : null;
                e.Response = core.Environment.CreateWebResourceResponse(
                    new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html ?? "")), html == null ? 404 : 200,
                    html == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
            };
        };
    }

    public static async Task Run(TableBrowser browser, Button toggle, Label status, MatchBook book)
    {
        async Task WaitUntil(Func<bool> done)
        {
            var end = DateTime.UtcNow.AddSeconds(40);
            while (!done())
            {
                if (DateTime.UtcNow >= end) throw new TimeoutException("受控牌桌超时，最后状态为" + status.Text);
                await Task.Delay(100);
            }
        }
        await WaitUntil(() => toggle.Enabled && status.Text.Contains("牌桌已就绪"));
        if ((await browser.ReadAsync("Studio/88")).Kind != "ready")
            throw new Exception("错误桌号触发了自动操作");
        var ready = await browser.ReadAsync();
        await browser.CoreWebView2.ExecuteScriptAsync("window.ourTurn=true");
        if ((await browser.ReadAsync(ready.Table, "10/takeCards")).Kind != "stalled")
            throw new Exception("重载后阶段未变，应暂停而非再次提交抽牌。");
        await browser.CoreWebView2.ExecuteScriptAsync("window.ourTurn=false");
        toggle.PerformClick();
        await WaitUntil(() => status.Text.Contains("等待其他座位行动"));
        if (await browser.CoreWebView2.ExecuteScriptAsync("gameui.gamedatas.gamestate.name") != "\"takeCards\"")
            throw new Exception("在其他玩家回合执行了操作");
        // A reconnection on the same seat resumes; switching seats on the same table must disarm it.
        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#mine').dataset.playerId='20'");
        await WaitUntil(() => toggle.Text == "启动自动出牌" && status.Text.Contains("牌桌已就绪"));
        await browser.CoreWebView2.ExecuteScriptAsync("document.querySelector('#mine').dataset.playerId='10'");
        await Task.Delay(1100);
        toggle.PerformClick();
        await WaitUntil(() => status.Text.Contains("等待其他座位行动"));
        var reloaded = new TaskCompletionSource();
        void Loaded(object? _, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e) => reloaded.TrySetResult();
        browser.CoreWebView2.NavigationCompleted += Loaded;
        browser.CoreWebView2.Reload();
        try { await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { browser.CoreWebView2.NavigationCompleted -= Loaded; }
        await WaitUntil(() => toggle.Text == "暂停自动出牌" && status.Text.Contains("等待其他座位行动"));
        await browser.CoreWebView2.ExecuteScriptAsync("window.ourTurn=true");
        await WaitUntil(() => status.Text.Contains("战绩已保存"));
        var match = book.Load().Single().Match;
        if (browser.Source?.PathAndQuery != "/table?table=77" || toggle.Enabled || match.Site != "Studio" ||
            match.Table != "77" || !match.Players.SequenceEqual([new Seat("10","甲",1,43), new Seat("20","乙",2,37)]))
            throw new Exception("跳转结算页后的桌号、玩家、名次或比分有误");
        var log = Checks.Children(status.FindForm()!).OfType<RichTextBox>().Single(b => b.AccessibleName == "操作日志内容");
        var lines = log.Lines;
        if (lines.Count(s => s.Contains("选中浅蓝色螃蟹用于配对")) != 2 ||
            !lines.Any(s => s.Contains("等待服务器切换回合或结算")) ||
            !lines.Any(s => s.Contains("操作回执超时")) || lines.Count(s => s.Contains("从牌库抽牌")) != 1 ||
            !lines.Any(s => s.Contains("选择可抽牌的对手")) || !lines.Any(s => s.Contains("战绩已保存")) ||
            log.PointToScreen(Point.Empty).X < browser.PointToScreen(new Point(browser.Width, 0)).X)
            throw new Exception("右侧操作日志缺少连续动作、换轮、对手选择或结算信息，或位置不正确");
        Console.WriteLine("通过 · 桌面牌桌循环：座位切换、原桌重连、跨轮出牌、组合效果、右侧中文日志、终局自动收录 43 比 37。");
    }
}
