extern alias SeatWorker;
using System.Text;
using SeriesRunner = SeatWorker::SeriesRunner;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class RealtimeChecks
{
    private const string Lobby = """
        <!doctype html><meta charset=utf-8><script>window.globalUserInfos={id:"10"};</script>
        <div id="gamelobby-module">
        <button id="pageheader_simple">普通游戏</button><button id="pageheader_realtime">即时制</button>
        <button id="pageheader_automatic">自动对手</button>
        <div id="Xannaplay_1631"><button id="Xannaplay_pref_1631">匹配偏好</button></div></div>
        <div id="game_preferences_contents" hidden><button id="play_now">开始游戏</button></div>
        <script>
        const by=id=>document.getElementById(id), lobby=by("gamelobby-module");
        if(sessionStorage.awaitingTrophy==="1"){
            const splash=document.createElement("div");splash.id="splash_trophy_80";splash.className="splash_block";
            splash.style.cssText="position:fixed;inset:0;z-index:10;background:white";
            splash.innerHTML='<div id="splash_central_80">名誉值达到 80<button id="continue_btn_80">继续</button></div>';
            const overlay=document.createElement("div");overlay.id="splashedNotifications_overlay";
            overlay.style.cssText="position:fixed;inset:0;z-index:9;background:white";
            document.body.append(overlay,splash);by("continue_btn_80").onclick=()=>{
                delete sessionStorage.awaitingTrophy;fetch("/event?op=trophy");splash.remove();
                setTimeout(()=>overlay.remove(),750);};
        }
        for (const [id,mode] of [["simple","rankingmode_simple"],["realtime","gamemode_realtime"],["automatic","opponentmode_automatic"]]){
            by("pageheader_"+id).onclick=()=>lobby.classList.add(mode);
        }
        // 大厅可能在下一次复核前自行完成切换，此时不能把未派发的点击算作失败或再次提交。
        const firstMode=by("pageheader_simple"), isShown=firstMode.checkVisibility.bind(firstMode);
        firstMode.checkVisibility=opts=>{const shown=isShown(opts);
            if(shown&&!sessionStorage.modeRaced){sessionStorage.modeRaced="1";queueMicrotask(()=>lobby.classList.add("rankingmode_simple"));}
            return shown;};
        const prefs=by("game_preferences_contents");
        by("Xannaplay_pref_1631").onclick=()=>prefs.hidden=false;
        for(const [id,title,values] of [[299,"玩家人数",["2 玩家","3 玩家","4 玩家"]],
                [101,"盐",["关","开"]],[102,"椒",["关","开"]],[110,"双倍分数",["关","开"]]]){
            const group=document.createElement("div"); group.className="mm_prefs_wrapper";group.id="mm_prefs_wrapper_"+id;
            group.innerHTML='<div class="mm_prefs_option">'+title+'</div>'; prefs.prepend(group);
            values.forEach((label,i)=>{
                const value=id===299?i+2:i+1, key="mm_pref_"+id+"_value_"+value;
                const priority=sessionStorage[key] || "1";
                const row=document.createElement("div");row.className="mm_pref";
                row.innerHTML='<button id="'+key+'" class="mm_pref_value mm_pref_color_priority_'+priority+'">设置</button>'+
                    '<button hidden id="'+key+'_priority_9">不接受</button>';
                const text=document.createElement("b");text.className="option_value_label";text.textContent=label;
                group.append(row,text);
                by(key).onclick=()=>by(key+"_priority_9").hidden=false;
                by(key+"_priority_9").onclick=()=>{by(key+"_priority_9").hidden=true;
                    setTimeout(()=>{sessionStorage[key]="9";
                        if(id!==110)by(key).className="mm_pref_value mm_pref_color_priority_9";},450);};
            });
        }
        by("play_now").onclick=async()=>{
            if(prefs.querySelectorAll(".mm_pref_color_priority_9").length!==5)return;
            prefs.hidden=true;
            if(lobby.classList.length!==3)return;
            const n=Number(await (await fetch("/event?op=join")).text());
            const table=700+n, other=n===2?"30":"20";
            by("Xannaplay_1631").classList.add("wannaplay_waiting");
            const go=()=>location.assign("/1/seasaltpaper?table="+table);
            setTimeout(()=>{
                if(n===2){go();return;}
                window.mainsite={acceptGameStart:{tableinfos:{id:table,game_name:"seasaltpaper",players:{10:{},[other]:{}},
                    options:{101:{value:1},102:{value:1},110:{value:1},200:{value:1},201:{value:0}}}}};
                const offer=document.createElement("div");offer.className="gamestart_overlay";offer.id="gamestart_"+table;
                const accept=document.createElement("button");accept.id="ags_start_game_accept";accept.textContent="接受";
                accept.onclick=async()=>{await fetch("/event?op=accept&table="+table);accept.disabled=true;
                    if(n!==3){go();return;}
                    // 第三桌服务端接受成功，大厅丢失确认和跳转两条推送。
                };
                offer.append(accept);document.body.prepend(offer);
            },1200);
        };
        </script>
        """;

    public static int Run(bool voidFirst = false, string? stopAt = null, bool unknownEnd = false, string? endProof = null)
    {
        if (endProof is not (null or "normal" or "cancel" or "wrong-table" or "wrong-map" or "missing-map" or "wrong-ranking"))
            throw new ArgumentException("未知原桌结算场景。");
        string root = Directory.CreateTempSubdirectory("SeaSalt-realtime-three-").FullName;
        string batchId = Guid.NewGuid().ToString("N");
        int joins = 0, lobbyLoads = 0, allocatedChecks = 0, trophies = 0, slowReads = 0;
        var accepts = new List<string>();
        var draws = new List<string>();
        var kept = new List<string>();
        var finished = new List<string>();
        int rejected = 0;
        int terminalReads = 0, staleAccepts = 0, endedGameVisits = 0;
        bool observedPendingRead = false, heldUntilNavigation = false, releasedAfterRevocation = false;
        bool slowReturned = false;
        bool deliveredStaleBoard = false;
        int? stopCommandExit = null;
        string ledger = Path.Combine(root, "fixture-server.jsonl");
        void Record(object entry) => File.AppendAllText(ledger, System.Text.Json.JsonSerializer.Serialize(entry) + Environment.NewLine);
        Console.WriteLine("受控服务端账本 " + ledger);
        void StopQueue(string table)
        {
            // 与值守人使用相同的程序和命令，保持另一个进程中的在途提交及原桌出牌器。
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "SeatControl.exe"))
                { UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] {"--stop-queue", batchId, "--root", root}) start.ArgumentList.Add(arg);
            using var command = System.Diagnostics.Process.Start(start)!;
            if (!command.WaitForExit(6000)) throw new TimeoutException("批次停排命令没有返回。");
            stopCommandExit = command.ExitCode;
            Record(new { batchId, table, signal = "stop-queue", stopAt, stopCommandExit, at = DateTimeOffset.UtcNow });
        }
        void Configure(Desk desk)
        {
            var web = desk.GameBrowser;
            Task<string>? endObservation = null;
            desk.LogLine += message => {
                if (endProof != "wrong-ranking" || message != "本局已结束，正在打开官方结算页。") return;
                const System.Reflection.BindingFlags privateMember = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
                var lease = (Task)typeof(Desk).GetProperty("ControllerRevoked", privateMember)!.GetValue(desk)!;
                var toggle = (Button)typeof(Desk).GetField("toggle", privateMember)!.GetValue(desk)!;
                toggle.EnabledChanged += (_, _) => {
                    if (toggle.Enabled || desk.IsPlaying) return;
                    releasedAfterRevocation = lease.IsCompleted;
                    Record(new { batchId, signal = "original-seat-released", revoked = lease.IsCompleted, playing = desk.IsPlaying });
                };
                endObservation = LobbyControls.Run(web, "realtime", null);
                observedPendingRead = !endObservation.IsCompleted && desk.IsPlaying && !lease.IsCompleted;
                Record(new { batchId, signal = "old-game-observation-issued", observedPendingRead });
            };
            web.CoreWebView2InitializationCompleted += (_, initialized) =>
            {
                if (!initialized.IsSuccess) return;
                var core = web.CoreWebView2;
                // 夹具在 SetupBrowser 之前注册。因此该回调观测宿主 NavigationStarting 撤旧前一刻。
                core.NavigationStarting += (_, evt) => {
                    if (endObservation == null || new Uri(evt.Uri).AbsolutePath != "/table") return;
                    heldUntilNavigation = desk.IsPlaying;
                    Record(new { batchId, signal = "ending-before-nav-revocation", heldUntilNavigation, oldReadCompleted = endObservation.IsCompleted });
                };
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += async (_, e) =>
                {
                    var uri = new Uri(e.Request.Uri);
                    var fields = System.Web.HttpUtility.ParseQueryString(uri.Query);
                    string table = fields["table"] ?? "";
                    string other = table == "702" ? "30" : "20";
                    string? body = null;
                    if (uri.Host == "studio.boardgamearena.com")
                    {
                        if (endProof != null && finished.Contains(table) &&
                            (uri.AbsolutePath == "/tableview" || uri.AbsolutePath.EndsWith("/seasaltpaper"))) endedGameVisits++;
                        switch (uri.AbsolutePath)
                        {
                            case "/stale-accept": staleAccepts++; body = "{}"; break;
                            case "/terminal-authority" when finished.Contains(table):
                                terminalReads++;
                                string stage = endProof == "normal" && terminalReads == 1 ? "play" : table == "703" ? "archive" : "finished";
                                var fieldsForEnd = new Dictionary<string, object> {
                                    ["id"] = endProof == "wrong-table" ? "999" : table, ["game_name"] = "seasaltpaper", ["status"] = stage,
                                    ["cancelled"] = voidFirst ? 1 : 0, ["result"] = new {endgame_reason = voidFirst ? "neutralized_after_skipturn" : "normal_end"},
                                    ["options"] = new Dictionary<int, object>{{101,new{value=1}},{102,new{value=1}},{110,new{value=1}},{200,new{value=1}},{201,new{value=0}}} };
                                if (endProof != "missing-map") fieldsForEnd["players"] = new Dictionary<string, object> {
                                    ["10"] = new { }, [endProof == "wrong-map" ? "99" : other] = new { } };
                                Record(new { batchId, table, signal = "terminal-authority", endProof, stage, terminalReads, fieldsForEnd });
                                body = System.Text.Json.JsonSerializer.Serialize(fieldsForEnd);
                                break;
                            case "/allocation-authority" when table == "703" && accepts.Contains(table):
                                Record(new { batchId, table, signal = "fresh-allocation-authority", status = "play", at = DateTimeOffset.UtcNow });
                                body = System.Text.Json.JsonSerializer.Serialize(new { id = table, game_name = "seasaltpaper", status = "play",
                                    players = new Dictionary<string,object>{{"10",new{}},{"20",new{}}},
                                    options = new Dictionary<string,object>{{"101",new{value=1}},{"102",new{value=1}},
                                        {"110",new{value=1}},{"200",new{value=1}},{"201",new{value=0}}} });
                                break;
                            case "/fault/slow-table-read":
                                // 真实第二起故障从刷新到重新就绪共 22 秒。挂起 WebView 读响应但保持 UI 消息泵运行。
                                var deferral = e.GetDeferral();
                                try
                                {
                                    slowReads++;
                                    await Task.Delay(TimeSpan.FromSeconds(22));
                                    slowReturned = true;
                                    Record(new { batchId, table = "702", signal = "slow-read-returned", at = DateTimeOffset.UtcNow });
                                    e.Response = core.Environment.CreateWebResourceResponse(null, 204, "No Content", "");
                                }
                                finally { deferral.Complete(); }
                                return;
                            case "/lobby": lobbyLoads++; body = Lobby; break;
                            case "/event":
                                switch (fields["op"])
                                {
                                    case "join": joins++; break;
                                    case "accept": accepts.Add(table); break;
                                    case "trophy": trophies++; break;
                                }
                                Record(new { batchId, table, lobbyEvent = fields["op"], joins, at = DateTimeOffset.UtcNow });
                                if (stopAt == "join" && joins == 1 && fields["op"] == "join") StopQueue(table);
                                body = joins.ToString();
                                break;
                            case "/turn":
                                string? action = fields["action"];
                                bool accepted = e.Request.Method == "POST" && fields["seat"] == "10" && table == (700 + joins).ToString() &&
                                    (action switch
                                    {
                                        "draw" => !draws.Contains(table) && (table != "703" || allocatedChecks > 0),
                                        "keep" => draws.Contains(table) && !kept.Contains(table) &&
                                            fields["card"] == "crab-two" && (table != "702" || slowReturned),
                                        "finish" => kept.Contains(table) && !finished.Contains(table),
                                        _ => false
                                    });
                                if (!accepted) rejected++;
                                else if (action == "draw") draws.Add(table);
                                else if (action == "keep") kept.Add(table);
                                else finished.Add(table);
                                string next = !accepted ? "rejected" : action == "draw" ? "chooseCard" :
                                    action == "keep" ? "putDiscardPile" : "gameEnd";
                                Record(new { batchId, table, seat = fields["seat"], action, card = fields["card"],
                                    accepted, next, slowReturned, allocatedChecks, at = DateTimeOffset.UtcNow });
                                if (stopAt == "draw" && accepted && action == "draw" && table == "701") StopQueue(table);
                                body = System.Text.Json.JsonSerializer.Serialize(new { accepted, next });
                                break;
                            case "/1/seasaltpaper" when table == (700 + joins).ToString():
                                // 两轮牌局沿用单桌回归，包括操作已提交但回执丢失后的原桌重载。
                                // 重载后保留的两张牌由宿主已接受的抽牌命令决定，页面缓存不能生成确认。
                                bool showDraw = draws.Contains(table) && !kept.Contains(table);
                                if (stopAt == "unresolved" && showDraw && !deliveredStaleBoard)
                                {
                                    deliveredStaleBoard = true;
                                    showDraw = false;
                                    Record(new { batchId, table, signal = "stale-board-after-accepted-draw", at = DateTimeOffset.UtcNow });
                                }
                                body = TableScenario.Page.Replace("if(sessionStorage.awaitingPick===\"1\")",
                                    showDraw ? "if(true)" : "if(false)") + $$"""
                                    <script>window.globalUserInfos={id:"10"};
                                    window.bgaGameData={isRealTime:true};
                                    Object.assign(gameui,{player_id:"10",table_id:"{{table}}",game_name:"seasaltpaper",
                                        bRealtime:1,hotseat_interface:"normal",hotseatplayers:[]});
                                    gameui.gamedatas.players={"10":{}, "{{other}}":{} };
                                    if("{{table}}"==="702"){
                                        Object.defineProperty(window.globalUserInfos,"id",{get(){
                                            if(sessionStorage.slowRead==="armed"){
                                                sessionStorage.slowRead="done";
                                                const block=new XMLHttpRequest();block.open("GET","/fault/slow-table-read",false);block.send();
                                            }
                                            return "10";
                                        } });
                                    }
                                    if("{{table}}"==="702" && state.name==="chooseCard"){
                                        state.name="chooseCardNewVersion";
                                        document.getElementById("shell").classList.add("bga-cards_disabled-card");
                                    }
                                    setTimeout(()=>window.ourTurn=true,1400);
                                    const submit=(action,card="")=>fetch("/turn?seat=10&table={{table}}&action="+action+"&card="+card,
                                        {method:"POST"}).then(r=>r.json());
                                    document.getElementById("deck-top").addEventListener("click",()=>{
                                        if("{{table}}"==="702"&&!sessionStorage.slowRead)sessionStorage.slowRead="armed";
                                        submit("draw");
                                    });
                                    document.getElementById("pick").addEventListener("click",ev=>{
                                        const card=ev.target.closest(".card");
                                        if(card)submit("keep",card.id);
                                    },true);
                                    const end=finish; finish=async()=>{const ack=await submit("finish");
                                        if(!ack.accepted||ack.next!=="gameEnd")return;
                                        if("{{table}}"==="701")sessionStorage.awaitingTrophy="1";end();};</script>
                                    """;
                                break;
                            case "/table" when table == "703" && !finished.Contains(table) && accepts.Contains(table):
                                allocatedChecks++;
                                body = """
                                    <!doctype html><script>window.globalUserInfos={id:"10"};
                                    window.mainsite={ajaxcall:(url,args,owner,reply,error)=>fetch("/allocation-authority?table="+args.id)
                                        .then(r=>r.json()).then(reply).catch(()=>error?.(true))};</script>
                                    <div class="tablewithgame_seasaltpaper"><div id="players_at_table">
                                    <div class="active_player" id="active_player_10"></div>
                                    <div class="active_player" id="active_player_20"></div></div>
                                    <button id="access_game_normal" onclick="location.assign('/tableview?table=703')">进入游戏</button></div>
                                    """;
                                break;
                            case "/tableview" when table == "703":
                                body = """
                                    <!doctype html><script>window.globalUserInfos={id:"10"};</script>
                                    <iframe id="gameIframe" src="/1/seasaltpaper?table=703" width="1100" height="720"></iframe>
                                    """;
                                break;
                            case "/table" when finished.Contains(table):
                                string authority = endProof == null ? $$"""
                                    window.mainsite={ajaxcall:(url,params,owner,reply)=>{if(url==="/table/table/tableinfos.html"&&params.id==={{table}})
                                        setTimeout(()=>reply({id:params.id,game_name:"seasaltpaper",status:"finished",cancelled:{{(voidFirst ? 1 : 0)}},
                                        players:{10:{},{{other}}:{} },result:{endgame_reason:"{{(voidFirst ? "neutralized_after_skipturn" : unknownEnd ? "" : "normal_end")}}"} }),300);} };
                                    """ : $$$"""
                                    window.mainsite={ajaxcall:(url,p,owner,reply,done)=>fetch('/terminal-authority?table='+p.id).then(r=>r.json()).then(reply).catch(()=>done?.(true)),
                                        acceptGameStart:{tableinfos:{id:{{{table}}},game_name:'seasaltpaper',players:{10:{decision:1},{{{other}}}:{} },
                                        options:{101:{value:1},102:{value:1},110:{value:1},200:{value:1},201:{value:0} } } } };
                                    """;
                                string overlay = endProof == null ? "" : $"<div class='gamestart_overlay game_about_to_start' id='gamestart_{table}'><button id=ags_start_game_accept onclick=\"fetch('/stale-accept')\">接受</button></div>";
                                body = $$"""
                                    <!doctype html><script>window.globalUserInfos={id:"10"};
                                    {{authority}}</script>{{overlay}}<div id="gametable_box" class="gametable_status_finished"></div>
                                    <div class="tablewithgame_seasaltpaper"><div id="game_result">
                                    <div id="score_entry_{{(endProof == "wrong-ranking" ? "99" : other)}}"><span class="rank">1</span><a class="playername">对手</a><span class="score">44</span></div>
                                    <div id="score_entry_10"><span class="rank">2</span><a class="playername">己方</a><span class="score">36</span></div>
                                    </div></div>
                                    """;
                                break;
                        }
                    }
                    e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body ?? "")),
                        body == null ? 404 : 200, body == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
                };
            };
        }
        int exit = SeriesRunner.MainSeries(["--realtime","3","--site","Studio","--batch-id",batchId,"--root",root], Configure);
        var book = new SeriesBook(root);
        var run = book.Load(batchId);
        string[] tables = ["701","702","703"];
        var matches = new MatchBook(Path.Combine(root,"matches")).Load();
        string log = File.ReadAllText(Path.Combine(root,"series",batchId+".log"));
        string incidentsPath = Path.Combine(root,"series",batchId+".incidents.jsonl");
        var incidents = (File.Exists(incidentsPath) ? File.ReadLines(incidentsPath) : Array.Empty<string>())
            .Select(line=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(line)).ToArray();
        if (endProof != null)
        {
            Record(new { batchId, endProof, exit, terminalReads, staleAccepts, endedGameVisits, observedPendingRead, heldUntilNavigation, releasedAfterRevocation, joins, run.CurrentTable, run.Completed,
                matches = matches.Count, stopped = book.QueueStopped(batchId) });
            if (endProof == "wrong-ranking" && (!observedPendingRead || !heldUntilNavigation || !releasedAfterRevocation))
                throw new Exception("原桌终局导航前旧读仍有效时，席位已经被提前释放："+root);
            if (terminalReads < 1 || staleAccepts != 0 || endedGameVisits != 0) throw new Exception("原桌结算缺少权威读、重按旧确认或重新进入结束桌："+root);
            if (endProof is "wrong-map" or "missing-map" or "wrong-ranking" or "wrong-table") {
                if (exit != 1 || !book.QueueStopped(batchId) || joins != 1 || run.CurrentTable != "701" || run.Completed != 0 || matches.Count != 0 ||
                    !incidents.Any(e=>e.GetProperty("code").GetString()=="official-bound-table-conflict")) throw new Exception("原桌结算身份冲突未安全停排："+root);
                Console.WriteLine("通过 · 原桌终局身份拒绝，保留关联与零战绩。"+endProof); return 0;
            }
        }
        if (stopAt != null)
        {
            // 重启同批次仍停在收尾位置，进入 UI 或打开大厅即失败。
            int resume = SeriesRunner.MainSeries(["--resume",batchId,"--root",root], _ => throw new Exception("停排后重新打开了大厅。"));
            Record(new { batchId, signal = "stop-result", stopAt, exit, resume, joins, run.Completed, run.Finished,
                matches = matches.Count, run.QueuePending, run.CurrentTable, run.OfferTable });
            bool stopped = stopAt == "unresolved" ? deliveredStaleBoard &&
                incidents.Count(e=>e.GetProperty("code").GetString()=="receipt-unresolved-after-authoritative-read")==1 : stopCommandExit == 0;
            if (!stopped || exit != 4 || resume != 4 || !book.QueueStopped(batchId) ||
                run.Finished || run.Completed != 1 || joins != 1 || !draws.SequenceEqual(["701"]) || !kept.SequenceEqual(["701"]) ||
                !finished.SequenceEqual(["701"]) || matches.Count != 1 || run.CurrentTable != null || run.QueuePending ||
                run.OfferTable != null || rejected != 0)
                throw new Exception("停排后原桌未完整收尾、重启恢复了新匹配或仍有在途桌：" + root);
            Console.WriteLine("通过 · 在桌停排后继续收牌、结算入库，一次入队。同批次重启继续停排。证据 " + ledger);
            return 0;
        }
        if (voidFirst || unknownEnd)
        {
            if (exit != 1 || joins != 1 || run.Completed != 0 || matches.Count != 0 || rejected != 0 ||
                !draws.SequenceEqual(["701"]) || !kept.SequenceEqual(["701"]) || !finished.SequenceEqual(["701"]) ||
                !incidents.Any(e=>e.GetProperty("code").GetString()==(voidFirst ? "official-void-or-skipturn" : "official-non-normal-end") &&
                    e.GetProperty("table").GetString()=="701"))
                throw new Exception("跳过回合的官方异常终局未正确记录或仍然入库、继续匹配：" + root);
            Console.WriteLine($"通过 · {(voidFirst ? "跳过回合" : "缺少结束原因")}的官方终局已记异常，零完赛、零正常战绩、没有第二次排队。证据 " + root);
            return 0;
        }
        if (exit != 0) throw new Exception("即时制后台退出，检查受控批次日志：" + root);
        bool recoveredSlowTable = incidents.Any(e=>e.GetProperty("code").GetString()=="page-read-slow" &&
                e.GetProperty("table").GetString()=="702" && e.GetProperty("actions").GetInt32()>0) &&
            incidents.Any(e=>e.GetProperty("code").GetString()=="page-read-returned" &&
                e.GetProperty("table").GetString()=="702" && e.GetProperty("elapsedMs").GetInt64()>=21000);
        if (!run.Finished || run.CurrentTable != null || run.QueuePending || run.OfferTable != null ||
            !run.Realtime || run.Site != "Studio" || joins != 3 || lobbyLoads != 4 || allocatedChecks != 1 || trophies != 1 ||
            slowReads != 1 || !recoveredSlowTable || !log.Contains("保底行动") || !accepts.SequenceEqual(["701","703"]) ||
            rejected != 0 || !draws.SequenceEqual(tables) || !kept.SequenceEqual(tables) || !finished.SequenceEqual(tables) || matches.Count != 3 ||
            !run.Rounds.Select(r=>r.Table).SequenceEqual(tables) ||
            !run.Rounds.Select(r=>r.Guest.Id).SequenceEqual(["20","30","20"]) ||
            run.Rounds.Any(r=>r.Host.Id!="10" || r.Host.Score!=36 || r.Host.Rank!=2 || r.Guest.Score!=44 || r.Guest.Rank!=1))
            throw new Exception("即时制三桌的入队、接受、出牌、对手或成绩核对失败：" + root);
        Console.WriteLine("通过 · 一次启动，3 次入队，2 次确认和 1 次直接分桌，每桌原桌重载后打完两轮并入库，第三桌后停止。");
        Console.WriteLine("受控批次证据：" + Path.Combine(root,"series",batchId + ".json"));
        return 0;
    }
}
