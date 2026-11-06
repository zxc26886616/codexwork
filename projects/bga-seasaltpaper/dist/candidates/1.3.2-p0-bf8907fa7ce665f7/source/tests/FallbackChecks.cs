extern alias SeatWorker;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class FallbackChecks
{
    public static int Run(string mode)
    {
        if (mode is not ("offer" or "prearm-hang" or "pending" or "pending-same" or "pending-penalty" or "pending-offline" or "wrong-table" or "wrong-opponent" or "penalty" or "ended"))
            throw new ArgumentException("未知独立救场场景。");
        string root = Directory.CreateTempSubdirectory("SeaSalt-fallback-").FullName;
        string batch = Guid.NewGuid().ToString("N"), journal = Path.Combine(root,"seat-actions","Studio-991-10.json");
        var book = new SeriesBook(root); book.Start(3,"10","",batch,"Studio"); book.Queue(batch,true);
        var clock = Stopwatch.StartNew();
        bool pendingMode = mode.StartsWith("pending"), same = mode == "pending-same", prearm = mode == "prearm-hang";
        int accepts=0, actions=0, duplicates=0, joins=0, loads=0, authority=0, created=0;
        long acceptedAt=-1, readyAt=-1, faultAt=-1;
        bool fixtureClosed=false, sameBrowser=true, preservedPending=true, faultedArmed=false, boundAtPenalty=false, sawOffline=false;
        string? originalIntent=null;
        bool blockedRead=false, oldFrameRetired=false;
        int oldProcessId=0;uint oldFrame=0;
        CoreWebView2Environment? oldEnvironment=null;
        FileStream? statusLock=null;
        Desk? activeDesk=null; TableBrowser? originalBrowser=null;
        void Log(string s) { s=$"{clock.Elapsed.TotalSeconds:F3}s {s}";Console.WriteLine(s);File.AppendAllText(Path.Combine(root,"authority.txt"),s+Environment.NewLine); }
        string OfficialStatus()=>mode=="ended"||actions>=2?"finished":acceptedAt<0||clock.ElapsedMilliseconds<acceptedAt+2000?"ready":"play";
        object Official()=>new {id=mode=="wrong-table"?992:991,game_name="seasaltpaper",status=OfficialStatus(),cancelled=mode=="ended"?1:0,
            players=new Dictionary<string,object>{{"10",new{}},{mode=="wrong-opponent"?"30":"20",new{}}},
            options=new Dictionary<int,object>{{101,new{value=1}},{102,new{value=1}},{110,new{value=1}},{200,new{value=1}},{201,new{value=0}}},
            result=new{endgame_reason=mode=="ended"?"neutralized_after_skipturn":"normal_end"}};
        const string Bootstrap="""
            <script>globalUserInfos={id:"10"};mainsite={acceptGameStart:{tableinfos:{id:991,game_name:"seasaltpaper",status:"ready",
              players:{10:{decision:1},20:{decision:1}},options:{101:{value:1},102:{value:1},110:{value:1},200:{value:1},201:{value:0}}}},
              ajaxcall:(u,a,o,success,done)=>fetch('/authority').then(r=>r.json()).then(q=>{success(q);done?.(false);}).catch(()=>done?.(true))};</script>
            """;
        void Fault(Desk desk)
        {
            if(statusLock!=null||faultAt>=0)return;
            var state=book.Load(batch); faultAt=clock.ElapsedMilliseconds;faultedArmed=desk.IsPlaying;
            originalIntent=File.Exists(journal)?File.ReadAllText(journal):null;
            statusLock=new FileStream(Path.Combine(root,"series",batch+".status.json"),FileMode.Open,FileAccess.Read,FileShare.Read);
            Log($"FAULT locked-status offer={state.OfferTable} current={state.CurrentTable} armed={desk.IsPlaying} actions={desk.MatchedActions} pending={originalIntent!=null} browser={System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(desk.GameBrowser)}");
            _=Release();
            async Task Release(){
                var until=clock.ElapsedMilliseconds+3000;
                string incidents=Path.Combine(root,"series",batch+".incidents.jsonl");
                while(clock.ElapsedMilliseconds<until&&!desk.IsDisposed){
                    if(File.Exists(incidents)&&File.ReadAllText(incidents).Contains("supervisor-fallback-started")) break;
                    await Task.Delay(50);
                }
                statusLock?.Dispose();statusLock=null;Log("status lock released");
            }
        }
        void Configure(Desk desk)
        {
            activeDesk=desk;originalBrowser=desk.GameBrowser;
            desk.BrowserCreated+=replacement=>{created++;Install(replacement);};
            desk.LogLine+=s=>{if(s.Contains("网络已断开，等待恢复连接"))sawOffline=true;Log("desk "+s);};
            Install(originalBrowser);
            void Install(TableBrowser view){ view.CoreWebView2InitializationCompleted+=(_,init)=>{
                if(!init.IsSuccess)return;
                var core=view.CoreWebView2;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{
                    var uri=new Uri(e.Request.Uri);string? body=null;
                    if(uri.AbsolutePath=="/lobby")body=Bootstrap+(prearm?"""
                        <script>Object.defineProperty(globalUserInfos,'id',{get(){if(window.afterOffer){
                            window.afterOffer=false;const r=new XMLHttpRequest();r.open('GET','/hold-read',false);r.send();while(true){} }
                            return '10';}});</script>
                        """:"")+"""
                        <div class=gamestart_overlay id=gamestart_991><button id=ags_start_game_accept onclick="const r=new XMLHttpRequest();r.open('POST','/accept',false);r.send();this.remove();window.afterOffer=true">接受</button><div id=ags_start_game_status_wrap></div></div>
                        """;
                    else if(uri.AbsolutePath=="/hold-read"){
                        var saved=book.Load(batch);blockedRead=saved.OfferTable=="991"&&saved.CurrentTable==null&&!desk.IsPlaying;
                        oldProcessId=(int)core.BrowserProcessId;oldFrame=core.FrameId;oldEnvironment=core.Environment;
                        Log($"RENDERER hang original read pure=true offer={saved.OfferTable} current={saved.CurrentTable} armed={desk.IsPlaying} frame={oldFrame} pid={oldProcessId}");body="{}";
                    }
                    else if(uri.AbsolutePath=="/accept"){
                        accepts++;acceptedAt=clock.ElapsedMilliseconds;readyAt=acceptedAt+2000;Log("server accepted offer");
                        if(!pendingMode&&!prearm)Fault(desk);body="{}";
                    }
                    else if(uri.AbsolutePath=="/table"){
                        if(mode=="penalty")body="<script>location.href='/penalty?id=PRIVATE';</script>";
                        else body=Bootstrap+"<div class=tablewithgame_seasaltpaper><div id=gametable_box class=gametable_status_ready></div></div>";
                        Log($"server TABLE status={OfficialStatus()} actions={actions}");
                    }
                    else if(uri.AbsolutePath=="/authority"){
                        authority++;if(OfficialStatus()=="play"&&readyAt<0)readyAt=clock.ElapsedMilliseconds;
                        body=JsonSerializer.Serialize(Official());Log("server OFFICIAL "+OfficialStatus());
                    }
                    else if(uri.AbsolutePath=="/tableview")body=Bootstrap+"<iframe id=gameIframe src='/1/seasaltpaper?table=991' width=1100 height=700></iframe>";
                    else if(uri.AbsolutePath=="/1/seasaltpaper"){
                        loads++;int version=same?0:actions;
                        if(originalIntent!=null&&actions==1&&File.Exists(journal))preservedPending &= File.ReadAllText(journal)==originalIntent;
                        body=Bootstrap+$$$"""
                            <span id=move_nbr>{{{version+1}}}</span><span id=timeToThink_10>00:07</span>
                            <div id=deck style="width:55px;height:55px">抽牌</div><div id=discard1></div><div id=discard2></div>
                            <div class=player-table><div data-my-hand=true data-player-id=10><div class=card id=held{{{version}}} style="height:12px">牌</div></div></div>
                            <script>bgaGameData={isRealTime:true};
                            gameui={player_id:10,table_id:991,game_name:"seasaltpaper",bRealtime:true,gamedatas:{players:{10:{},20:{}},
                              gamestate:{id:1,name:"takeCards",type:"activeplayer",active_player:10,args:{canTakeFromDeck:true,canTakeFromDiscard:[]} } },
                              isCurrentPlayerActive:()=>gameui.gamedatas.gamestate.active_player===10};
                            gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);
                            deck.onclick=()=>{const r=new XMLHttpRequest();r.open('POST','/action?v='+Number(move_nbr.textContent-1),false);r.send();
                              if(r.responseText==='end'){gameui.gamedatas.gamestate.name='gameEnd';gameui.last_server_state=structuredClone(gameui.gamedatas.gamestate);}
                              else if(['offer','prearm-hang'].includes('{{{mode}}}')){move_nbr.textContent='2';held0.id='held1';}
                              else if('{{{mode}}}'==='pending-offline'){
                                const warning=document.createElement('div');warning.id='connect_gs_status';warning.textContent='offline';document.body.append(warning);
                                setTimeout(()=>warning.remove(),1800);
                              }
                            };
                            </script>
                            """;
                        Log($"server GAME loads={loads} version={version} pending-preserved={preservedPending}");
                    }
                    else if(uri.AbsolutePath=="/action"){
                        int version=int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["v"]!);
                        long remaining=readyAt+7000-clock.ElapsedMilliseconds;
                        if(version!=actions||actions>=2||actions==0&&remaining<=0)duplicates++;else actions++;
                        sameBrowser &= ReferenceEquals(originalBrowser,desk.GameBrowser);
                        if(prearm&&created==1&&actions==1){
                            async Task CheckRetired(){
                                try { using var oldProcess=Process.GetProcessById(oldProcessId);oldFrameRetired=oldProcess.HasExited; }
                                catch(ArgumentException){oldFrameRetired=true;}
                                if(!oldFrameRetired&&oldEnvironment!=null)try {
                                    var ps=await oldEnvironment.GetProcessExtendedInfosAsync().WaitAsync(TimeSpan.FromSeconds(2));
                                    oldFrameRetired=oldFrame!=0&&!ps.Any(i=>i.AssociatedFrameInfos.Any(f=>f.FrameId==oldFrame));
                                } catch(System.Runtime.InteropServices.COMException){ }
                                Log($"old controller disposed={originalBrowser.IsDisposed} retired-frame={oldFrameRetired}");
                            }
                            _=CheckRetired();
                        }
                        Log($"server ACTION accepted={actions} duplicate={duplicates} deadlineRemaining={remaining}ms sameWebView={sameBrowser} hostActions={desk.MatchedActions}");
                        if(pendingMode&&actions==1)Fault(desk);
                        if(mode=="pending-penalty"&&actions==1){
                            async Task Redirect(){await Task.Delay(600);core.Navigate("https://studio.boardgamearena.com/penalty?id=PRIVATE");}
                            _=Redirect();
                        }
                        body=actions>=2?"end":"ok";
                    }
                    else if(uri.AbsolutePath=="/penalty"){boundAtPenalty=desk.IsPlaying;Log("server PENALTY bound="+boundAtPenalty);body="<div>处罚提醒，仅报告</div>";}
                    else if(uri.AbsolutePath=="/join"){joins++;body="{}";}
                    e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body??"")),body==null?404:200,"OK","Content-Type:text/html; charset=utf-8\r\nCache-Control:no-store");
                };
            }; }
            desk.Shown+=async(_,_)=>{
                var until=DateTime.UtcNow.AddSeconds(same?17:prearm?18:25);
                while(!desk.IsDisposed&&DateTime.UtcNow<until)await Task.Delay(100);
                if(!desk.IsDisposed){fixtureClosed=true;desk.Close();}
            };
        }
        Log($"mode={mode} root={root} appSha={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(Desk).Assembly.Location)))} controlSha={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(SeatWorker::SeriesRunner).Assembly.Location)))}");
        int exceptionCount=0;
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> onException=(_,e)=>{
            if(faultAt>=0&&exceptionCount<6&&e.Exception is IOException or UnauthorizedAccessException){
                exceptionCount++;Log($"first-chance {e.Exception.GetType().FullName} hr=0x{e.Exception.HResult:X8} stack={e.Exception.StackTrace}");
            }
        };
        AppDomain.CurrentDomain.FirstChanceException+=onException;
        int exit=SeatWorker::SeriesRunner.MainSeries(["--realtime","3","--site","Studio","--batch-id",batch,"--root",root],Configure);
        AppDomain.CurrentDomain.FirstChanceException-=onException;
        statusLock?.Dispose();
        string evidencePath=Path.Combine(root,"series",batch+".fallback.jsonl");
        string events=File.Exists(evidencePath)?File.ReadAllText(evidencePath):"";
        var final=book.Load(batch);bool negative=mode is "wrong-table" or "wrong-opponent" or "penalty" or "ended";
        bool ok=events.Contains("supervisor-fallback-started")&&(prearm?blockedRead&&events.Contains("PrearmObservationTimeoutException"):
            events.Contains("System.IO.IOException")||events.Contains("System.UnauthorizedAccessException"))&&book.QueueStopped(batch)&&
            final.Completed==0&&accepts==1&&joins==0&&duplicates==0&&(sameBrowser&&created==0||prearm&&created==1&&oldFrameRetired)&&
            (negative?actions==0&&!fixtureClosed&&events.Contains(mode=="ended"?"supervisor-fallback-ended":"supervisor-fallback-rejected"):
             same?actions==1&&fixtureClosed&&preservedPending&&File.ReadAllText(journal)==originalIntent&&!events.Contains("supervisor-fallback-armed"):
             actions==2&&!fixtureClosed&&exit==4&&events.Contains("supervisor-fallback-ended")&&
                (pendingMode?faultedArmed&&preservedPending&&!events.Contains("supervisor-fallback-armed"):!faultedArmed&&events.Contains("supervisor-fallback-armed")))&&
            (mode!="pending-penalty"||boundAtPenalty)&&(mode!="pending-offline"||sawOffline);
        Log($"{(ok?"PASS":"FAIL")} exit={exit} accepts={accepts} actions={actions} dup={duplicates} sameWebView={sameBrowser} newControllers={created} stop={book.QueueStopped(batch)} completed={final.Completed} offer={final.OfferTable} current={final.CurrentTable} pendingPreserved={preservedPending} fixtureClosed={fixtureClosed}");
        return ok?0:1;
    }
}
