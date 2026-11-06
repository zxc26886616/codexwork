extern alias SeatWorker;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class AllocationChecks
{
    public static int Run(string mode)
    {
        string root=Directory.CreateTempSubdirectory("SeaSalt-allocation-").FullName;
        string batch=Guid.NewGuid().ToString("N");
        var book=new SeriesBook(root);
        var initial=book.Start(3,"10","",batch,"Studio");book.Queue(batch,true);
        var timer=Stopwatch.StartNew();
        bool frameCase=mode is "frame-once" or "frame-stuck" or "frame-slow" or "frame-blank" or "frame-absent" or "frame-wrong";
        long acceptedAt=-1, playAt=-1;
        int accepts=0, actions=0, duplicates=0, queries=0, tableGets=0, viewGets=0, maxActiveQueries=0, activeQueries=0;
        bool finished=false, preservedAtDeadline=false, held=false;
        var clientRequests=new HashSet<string>();int maxClientRequests=0, cancelledQueries=0;
        string? failure=null;
        void Log(string s){s=$"{timer.Elapsed.TotalSeconds:F3}s {s}"; Console.WriteLine(s);File.AppendAllText(Path.Combine(root,"authority.txt"),s+Environment.NewLine);}
        string OfficialStatus()=>mode=="ended"?"finished":acceptedAt<0||!frameCase && timer.ElapsedMilliseconds<acceptedAt+(mode=="long-ready"?135000:8000)?"ready":"play";
        object Official()
        {
            string status=OfficialStatus();
            if(status=="play"&&playAt<0) playAt=frameCase?timer.ElapsedMilliseconds:acceptedAt+(mode=="long-ready"?135000:8000);
            return new { id=mode=="wrong-table"?992:991,game_name="seasaltpaper",status,cancelled=mode=="ended"?1:0,
                players=new Dictionary<string,object>{{"10",new{decision=1}}, {mode=="wrong-opponent"||mode=="frame-wrong"&&viewGets>0?"30":"20",new{decision=1}}},
                options=new Dictionary<int,object>{{101,new{value=1}},{102,new{value=1}},{110,new{value=1}},{200,new{value=1}},{201,new{value=0}}},
                result=new{endgame_reason=mode=="ended"?"neutralized_after_skipturn":""}};
        }
        string Snapshot="""
            <script>
            globalUserInfos={id:"10"};
            const initial={id:991,game_name:"seasaltpaper",status:"ready",players:{10:{decision:1},20:{decision:1}},
                options:{101:{value:1},102:{value:1},110:{value:1},200:{value:1},201:{value:0}}};
            let previousReady=null;
            window.mainsite={acceptGameStart:{tableinfos:initial},
                ajaxcall:(url,args,owner,success,complete)=>{
                    fetch("/authority?id="+args.id).then(r=>{if(!r.ok)throw Error("http");return r.json();}).then(q=>{
                        success(q);complete?.(false);
                        if("__MODE__"==="callback-late" && q.status==="play" && previousReady){
                            previousReady();fetch("/late-callback",{method:"POST"});
                        }
                        if(q.status==="ready")previousReady=()=>success(q);
                    }).catch(()=>complete?.(true));
                }};
            </script>
            """;
        Snapshot=Snapshot.Replace("__MODE__",mode);
        bool lateCallback=false;
        void Configure(Desk desk)
        {
            desk.LogLine+=s=>Log("desk "+s);
            desk.GameBrowser.CoreWebView2InitializationCompleted+=(_,init)=>{
                if(!init.IsSuccess)return;
                var core=desk.GameBrowser.CoreWebView2;
                core.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent").DevToolsProtocolEventReceived+=(_,ev)=>{
                    using var doc=JsonDocument.Parse(ev.ParameterObjectAsJson);
                    var q=doc.RootElement;string uri=q.GetProperty("request").GetProperty("url").GetString()??"";
                    if(new Uri(uri).AbsolutePath=="/authority"){
                        clientRequests.Add(q.GetProperty("requestId").GetString()!);
                        maxClientRequests=Math.Max(maxClientRequests,clientRequests.Count);
                        Log("client authority request active="+clientRequests.Count);
                    }
                };
                foreach(string name in new[]{"Network.loadingFinished","Network.loadingFailed"})
                    core.GetDevToolsProtocolEventReceiver(name).DevToolsProtocolEventReceived+=(_,ev)=>{
                        using var doc=JsonDocument.Parse(ev.ParameterObjectAsJson);var q=doc.RootElement;
                        if(clientRequests.Remove(q.GetProperty("requestId").GetString()!)){
                            if(name=="Network.loadingFailed")cancelledQueries++;
                            Log("client authority "+name+" active="+clientRequests.Count);
                        }
                    };
                _=core.CallDevToolsProtocolMethodAsync("Network.enable","{}");
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{
                    var u=new Uri(e.Request.Uri);string? body=null;
                    if(u.AbsolutePath=="/lobby")
                        body=Snapshot+"""
                        <div class=gamestart_overlay id=gamestart_991>
                        <button id=ags_start_game_accept onclick="fetch('/accept',{method:'POST'}).then(()=>{this.remove();})">接受</button>
                        <div id=ags_start_game_status_wrap>己方已确认</div></div>
                        """;
                    else if(u.AbsolutePath=="/accept"){
                        accepts++;if(acceptedAt>=0)duplicates++;else acceptedAt=timer.ElapsedMilliseconds;
                        Log($"server ACCEPT count={accepts}");body="{}";
                    }
                    else if(u.AbsolutePath=="/table"){
                        tableGets++;Log($"server TABLE GET count={tableGets} status={OfficialStatus()}");
                        // DOM保持初始ready（或play标记），可见overlay永久残留，绝不自行跳转。
                        string domStatus=mode=="overlay-play"?"play":"ready";
                        body=Snapshot+$"""
                        <div class="tablewithgame_seasaltpaper"><div id=gametable_box class="gametable_status_{domStatus}"></div></div>
                        <div id=players_at_table><div class=active_player id=active_player_10></div><div class=active_player id=active_player_20></div></div>
                        <a id=access_game_normal href="/tableview?table=991">进入</a>
                        <div class="gamestart_overlay game_about_to_start" id=gamestart_991 style="position:fixed;inset:0;background:white">
                        <div id=ags_start_game_status_wrap>己方已确认</div></div>
                        """;
                    }
                    else if(u.AbsolutePath=="/late-callback"){
                        lateCallback=true;Log("late old-ready callback executed in same document after new play callback");body="{}";
                    }
                    else if(u.AbsolutePath=="/authority"){
                        queries++;activeQueries++;maxActiveQueries=Math.Max(maxActiveQueries,activeQueries);
                        string content=JsonSerializer.Serialize(Official());int seq=queries;
                        Log($"server AUTH query={seq} status={OfficialStatus()} active={activeQueries}");
                        int delay=mode=="slow"?1700:80;
                        if(mode=="timeout"&&queries==1)delay=25000;
                        var gate=e.GetDeferral();
                        async Task Reply(){
                            await Task.Delay(delay);
                            try { e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(content)),mode=="failure"&&seq==1?503:200,"OK","Content-Type:application/json");
                                Log($"server AUTH reply={seq}");gate.Complete(); }
                            catch(Exception x){Log("server cancelled authority response "+x.GetType().Name);}
                            activeQueries--;
                        }
                        _=Reply();return;
                    }
                    else if(u.AbsolutePath=="/tableview"){
                        viewGets++;Log($"server VIEW GET status={OfficialStatus()}");
                        if(OfficialStatus()!="play" || mode=="bounce"&&viewGets==1)body="""<script>location.replace("/table?table=991&nr=true")</script>""";
                        else if(mode is "frame-blank" or "frame-absent") {
                            string placeholder=mode=="frame-blank"?"<iframe id=gameIframe></iframe>":"";
                            body=Snapshot+placeholder+"""
                            <script>setTimeout(()=>{const f=document.querySelector('#gameIframe')??document.body.appendChild(document.createElement('iframe'));
                            f.id='gameIframe';f.src='/1/seasaltpaper?table=991';},4000);</script>
                            """;
                        } else body=Snapshot+"""
                        <iframe id=gameIframe src="/1/seasaltpaper?table=991" width=1100 height=700></iframe>
                        """;
                    }
                    else if(u.AbsolutePath=="/1/seasaltpaper"){
                        body=Snapshot+"""
                        <span id=move_nbr>1</span><span id=timeToThink_10>00:07</span>
                        <div id=deck style="width:50px;height:50px">抽牌</div><div id=discard1></div><div id=discard2></div>
                        <div class=player-table><div data-my-hand=true data-player-id=10><div class=card id=held0 style="height:10px">牌</div></div></div>
                        <script>
                        bgaGameData={isRealTime:true};
                        gameui={player_id:10,table_id:991,game_name:"seasaltpaper",bRealtime:true,gamedatas:{
                          players:{10:{},20:{}},gamestate:{id:1,name:"takeCards",type:"activeplayer",active_player:10,
                          args:{canTakeFromDeck:true,canTakeFromDiscard:[]}}},isCurrentPlayerActive:()=>gameui.gamedatas.gamestate.active_player===10};
                        gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);
                        deck.onclick=()=>{const x=new XMLHttpRequest();x.open("POST","/action",false);x.send();
                          gameui.gamedatas.gamestate.active_player=20;gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);};
                        </script>
                        """;
                        if(frameCase) {
                            bool missing=mode=="frame-stuck" || mode is "frame-once" or "frame-wrong" && viewGets==1;
                            if(missing) body=Snapshot+"<div id=deck>waiting</div><script>window.gameui={};</script>";
                            if(mode=="frame-slow")body=body.Replace("gameui={player_id:", "window.gameui={game_name:'seasaltpaper',gamedatas:{}};setTimeout(()=>{gameui={player_id:")
                                .Replace("gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);};", "gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);};},4200);");
                            Log($"server game FRAME doc={viewGets} missing={missing} remainingMs={playAt+7000-timer.ElapsedMilliseconds}");
                        }
                    }
                    else if(u.AbsolutePath=="/action"){
                        long remaining=playAt+(mode=="bounce"?14000:7000)-timer.ElapsedMilliseconds;
                        if(OfficialStatus()!="play"||actions!=0||remaining<=0) {duplicates++;failure="late or repeated action";}
                        else actions++;
                        Log($"server ACTION accepted={actions} duplicate={duplicates} remaining={remaining}ms bound={desk.IsPlaying}");
                        body="{}";
                    }
                    e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body??"")),body==null?404:200,body==null?"Not Found":"OK","Content-Type:text/html; charset=utf-8\r\nCache-Control:no-store");
                };
            };
            desk.Shown+=async(_,_)=>{
                var limit=DateTime.UtcNow.AddSeconds(mode=="long-ready"?150:mode=="timeout"?45:22);
                while(!desk.IsDisposed && DateTime.UtcNow<limit && actions==0){
                    if(mode=="long-ready"&&acceptedAt>=0&&timer.ElapsedMilliseconds-acceptedAt>=128000&&!held){
                        var state=book.Load(batch);held=true;
                        preservedAtDeadline=!desk.IsDisposed&&state.OfferTable=="991"&&state.CurrentTable==null&&state.QueuePending&&book.QueueStopped(batch);
                        Log($"after original120s preserved={preservedAtDeadline} stop={book.QueueStopped(batch)} offer={state.OfferTable} current={state.CurrentTable}");
                    }
                    await Task.Delay(100);
                }
                if(!desk.IsDisposed){await Task.Delay(300);finished=true;desk.Close();}
            };
        }
        var app=typeof(Desk).Assembly;Log($"mode={mode} root={root} app-sha={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app.Location)))} mvid={app.ManifestModule.ModuleVersionId}");
        int exit=SeatWorker::SeriesRunner.MainSeries(["--realtime","3","--site","Studio","--batch-id",batch,"--root",root],Configure);
        var end=book.Load(batch);
        bool negative=mode is "ended" or "wrong-table" or "wrong-opponent" or "frame-wrong";
        string log=File.ReadAllText(Path.Combine(root,"series",batch+".log"));
        string expected=mode=="ended"?"官方已确认原匹配桌结束":mode=="wrong-table"?"官方回包的桌号或游戏":"官方原桌的双方席位";
        string incidents=Path.Combine(root,"series",batch+".incidents.jsonl");
        var alerts=File.Exists(incidents) ? File.ReadLines(incidents).Select(s=>JsonSerializer.Deserialize<JsonElement>(s))
            .Where(x=>x.GetProperty("code").GetString()=="allocated-play-seat-not-ready").ToArray() : [];
        bool ok=mode=="frame-stuck" ? actions==0 && duplicates==0 && accepts==1 && viewGets is >=2 and <=4 &&
            book.QueueStopped(batch) && end.QueuePending && end.OfferTable=="991" && end.CurrentTable==null && log.Contains("尚未读取己方棋钟") &&
            alerts.Length==1 && alerts[0].GetProperty("table").GetString()=="991" &&
            alerts[0].GetProperty("actions").GetInt32()==0 && alerts[0].GetProperty("elapsedMs").GetInt64() is >=8000 and <11000 &&
            alerts[0].GetProperty("officialTable") is var proof && proof.GetProperty("status").GetString()=="play" &&
            proof.GetProperty("table").GetString()=="991" && proof.GetProperty("self").GetString()=="10" &&
            proof.GetProperty("opponent").GetString()=="20" && proof.GetProperty("authorityAtUnixMs").GetInt64()>0 &&
            proof.GetProperty("clockStatus").GetString()=="not-read" :
            negative?actions==0&&viewGets==(mode=="frame-wrong"?1:0)&&end.Completed==0&&end.CurrentTable==null&&
            queries>0&&!finished&&book.QueueStopped(batch)&&log.Contains(expected):
            actions==1&&duplicates==0&&end.CurrentTable=="991"&&end.Completed==0&&accepts==1&&
            (mode!="long-ready"||preservedAtDeadline) && (mode!="callback-late"||lateCallback) &&
            (mode!="bounce"||viewGets==2) &&
            (mode!="frame-once"||viewGets==2 && tableGets==2 && queries==2 && !book.QueueStopped(batch)) &&
            (mode is not ("frame-slow" or "frame-blank" or "frame-absent") || viewGets==1 && tableGets==1) && alerts.Length==0 &&
            (mode!="timeout"||cancelledQueries>0&&maxClientRequests==1);
        Log($"{(ok?"PASS":"FAIL")} exit={exit} actions={actions} duplicates={duplicates} accept={accepts} queries={queries} views={viewGets} tableGets={tableGets} serverDeferrals={maxActiveQueries} maxClientRequests={maxClientRequests} cancelledQueries={cancelledQueries} stopped={book.QueueStopped(batch)} firstWindowClosedByFixture={finished} failure={failure}");
        return ok?0:1;
    }
}
