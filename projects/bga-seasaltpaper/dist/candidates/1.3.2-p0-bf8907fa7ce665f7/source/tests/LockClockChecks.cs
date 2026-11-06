using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class LockClockChecks
{
#if LOCK_CLOCK_PROBE
    [STAThread]
    public static int Main(string[] args) => Run(args.FirstOrDefault() ?? "locked");
#endif
    public static int Run(string mode)
    {
        ApplicationConfiguration.Initialize();
        string root = Directory.CreateTempSubdirectory("SeaSalt-lock-clock-").FullName;
        var url = new Uri("https://fixture.studio.boardgamearena.com/1/seasaltpaper?table=774");
        using var desk = new Desk(root, url, true);
        var started = Stopwatch.StartNew();
        var trace = new List<string>();
        void Log(string line)
        {
            line = $"{started.Elapsed.TotalSeconds:F3}s {line}";
            trace.Add(line); Console.WriteLine(line);
            File.AppendAllText(Path.Combine(root, "server-trace.txt"), line + Environment.NewLine);
        }
        var dll = typeof(Desk).Assembly;
        Log($"mode={mode} root={root} dll={dll.Location} sha256={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(dll.Location)))} mvid={dll.ManifestModule.ModuleVersionId}");
        int accepted=0, duplicates=0, expired=0, loads=0, unresolved=0, dropped=0;
        long deadline=long.MaxValue, firstAccepted=0, secondRemaining=0;
        bool queueStopped=false;
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var later = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        string journal = Path.Combine(root,"seat-actions","Studio-774-10.json");
        string? firstIntent=null;
        desk.LogLine += s=>Log("desk "+s);
        desk.MatchedReceiptUnresolved += (_, _) => { unresolved++; queueStopped=true; Log("incident unresolved-after-fresh-read; stop-admission=True"); };
        desk.GameBrowser.CoreWebView2InitializationCompleted += (_, init) =>
        {
            if (!init.IsSuccess) return;
            var core=desk.GameBrowser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var uri=new Uri(e.Request.Uri);
                string? body=null;
                if(uri==url)
                {
                    loads++;
                    int displayed = mode is "same" or "undelivered" || mode=="same-later" && loads==2 ? 0 : accepted;
                    long duration = mode=="cross" ? 11000 : 7000;
                    long remaining = deadline==long.MaxValue ? duration : deadline-started.ElapsedMilliseconds;
                    Log($"GET table document={loads} shown={displayed} accepted={accepted} remaining={remaining}ms");
                    body=$$"""
                    <!doctype html><meta charset=utf-8>
                    <span id=move_nbr>{{displayed+1}}</span>
                    <span id=timeToThink_10>01:20</span><span id=timeToThink_20>00:01</span>
                    <span id=connect_gs_status hidden>连接中</span>
                    <div id=deck style="width:50px;height:50px">抽牌</div><div id=discard1></div><div id=discard2></div>
                    <div class=player-table><div data-my-hand=true data-player-id=10><div class=card id="card-{{displayed}}" style="height:10px">牌</div></div></div>
                    <script>
                    window.bgaGameData={isRealTime:true};
                    window.version={{displayed}};window.locked=false;
                    window.gameui={player_id:10,table_id:774,game_name:"seasaltpaper",bRealtime:true,gamedatas:{
                        players:{10:{},20:{} },gamestate:{id:20,name:"takeCards",type:"activeplayer",active_player:10,args:{canTakeFromDeck:true,canTakeFromDiscard:[]} } },
                        isCurrentPlayerActive:()=>true,isInterfaceLocked:()=>locked};
                    gameui.last_server_state=JSON.parse(JSON.stringify(gameui.gamedatas.gamestate));
                    const mode="{{mode}}", initial={{remaining}};
                    let end=performance.now()+initial;
                    function showClock(){
                        if(mode==="unknown") document.getElementById("timeToThink_10")?.remove();
                        else if(mode==="invalid") timeToThink_10.textContent="—";
                        else if(mode==="client") timeToThink_10.textContent=gameui.on_client_state?"00:01":"01:20";
                        else if(mode==="client-short" && gameui.on_client_state) timeToThink_10.textContent="00:01";
                        else if(mode==="animation" || mode==="opponent") timeToThink_10.textContent="01:20";
                        else if(document.getElementById("timeToThink_10")){
                            const s=Math.max(0,Math.ceil((end-performance.now())/1000));
                            timeToThink_10.textContent=String(Math.floor(s/60)).padStart(2,"0")+":"+String(s%60).padStart(2,"0");
                        }
                    }
                    showClock();if(mode!=="unknown")setInterval(showClock,100);
                    deck.onclick=()=>{
                        const req=new XMLHttpRequest();req.open("POST","/act?version="+version+"&document={{loads}}",false);req.send();
                        if(version===0){
                            end=performance.now()+{{duration}};showClock();
                            if(mode.startsWith("client")) {
                                gameui.on_client_state=true;
                                if(mode==="client-opponent") gameui.last_server_state.active_player=20;
                                if(mode==="client-not-realtime") bgaGameData.isRealTime=false;
                                showClock();
                            }
                            else locked=true;
                            if(mode==="animation" || mode==="opponent" || mode==="animation-short"){
                                setTimeout(()=>{version=1;move_nbr.textContent="2";document.querySelector(".card").id="card-1";locked=false;},1200);
                            }
                        }else locked=true;
                    };
                    </script>
                    """;
                }
                else if(uri.Host==url.Host && uri.AbsolutePath=="/act")
                {
                    var q=System.Web.HttpUtility.ParseQueryString(uri.Query);
                    long remaining=deadline-started.ElapsedMilliseconds;
                    if(mode=="undelivered") {
                        dropped++; firstAccepted=started.ElapsedMilliseconds; deadline=firstAccepted+7000;
                        firstIntent=File.ReadAllText(journal); first.TrySetResult();
                        Log($"proxy dropped before authority attempts={dropped}; client cannot infer delivery");
                    }
                    else if(q["version"]!=accepted.ToString()) duplicates++;
                    else if(remaining<=0) expired++;
                    else
                    {
                        accepted++;
                        if(accepted==1)
                        {
                            firstAccepted=started.ElapsedMilliseconds;
                            long budget=mode=="cross"?11000:7000;
                            if(mode is "unknown" or "invalid" or "client" or "client-opponent" or "client-not-realtime") budget=12000;
                            if(mode is "same-later" or "animation" or "opponent") budget=60000;
                            deadline=firstAccepted+budget;
                            firstIntent=File.ReadAllText(journal);
                            first.TrySetResult();
                        }
                        else {secondRemaining=deadline-started.ElapsedMilliseconds;later.TrySetResult();}
                    }
                    Log($"POST version={q["version"]} document={q["document"]} accepted={accepted} duplicate={duplicates} expired={expired} remaining={deadline-started.ElapsedMilliseconds}ms");
                    body="{}";
                }
                e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body??"")),body==null?404:200,body==null?"Not Found":"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        };
        Exception? error=null;
        desk.Shown += async (_,_) =>
        {
            try
            {
                var limit=DateTime.UtcNow.AddSeconds(12);
                while(!desk.StartMatchedSeat("Studio","774","10","20")){if(DateTime.UtcNow>=limit)throw new TimeoutException("arm");await Task.Delay(100);}
                if(mode=="guards") {
                    async Task Guard(string expected,string js) {
                        await desk.GameBrowser.CoreWebView2.ExecuteScriptAsync(js);
                        var read=await desk.GameBrowser.ReadAsync("Studio/774/座位/10",null,"20");
                        var observed=System.Text.Json.JsonSerializer.SerializeToElement(read);
                        Log("clock-guard "+expected+" "+observed.GetRawText());
                        if(observed.GetProperty("ClockStatus").GetString()!=expected ||
                            observed.GetProperty("ClockSeconds").ValueKind!=System.Text.Json.JsonValueKind.Null ||
                            read.Kind=="action" || accepted!=0)throw new Exception("clock guard read stale/opponent value or sent action");
                    }
                    await Guard("not-own-turn","gameui.isCurrentPlayerActive=()=>false;gameui.last_server_state.active_player=20;");
                    await Guard("offline","connect_gs_status.hidden=false;");
                    await Guard("not-realtime","connect_gs_status.hidden=true;gameui.on_client_state=true;bgaGameData.isRealTime=false;");
                    await Guard("not-own-turn","bgaGameData.isRealTime=true;");
                    Log("PASS guards accepted=0; no opponent/offline/client clock published");
                    return;
                }
                await first.Task.WaitAsync(TimeSpan.FromSeconds(8));
                if(mode is "same" or "undelivered")
                {
                    limit=DateTime.UtcNow.AddSeconds(16);
                    while(unresolved==0 && DateTime.UtcNow<limit)await Task.Delay(100);
                    while(started.ElapsedMilliseconds-firstAccepted<9000) await Task.Delay(100);
                    if(unresolved!=1||!queueStopped||accepted!=(mode=="undelivered"?0:1)||duplicates!=0||
                        mode=="undelivered"&&dropped!=1||!desk.IsPlaying||File.ReadAllText(journal)!=firstIntent)
                        throw new Exception("same-state must retain intent/seat, stop admission, and never repeat");
                }
                else
                {
                    int wait=mode=="locked"?36:mode=="same-later"?28:18;
                    await later.Task.WaitAsync(TimeSpan.FromSeconds(wait));
                    await Task.Delay(100);
                    if(duplicates!=0||expired!=0||accepted!=2||!desk.IsPlaying)throw new Exception("authority did not accept exactly two legal actions");
                    if(mode is "animation" or "opponent" or "animation-short" && loads!=1)throw new Exception("short animation should resolve without reload");
                    if(mode=="same-later" && (unresolved!=1||!queueStopped))throw new Exception("first fresh unchanged state must stop admission");
                    if(mode is not ("animation" or "opponent" or "animation-short") && loads<2)throw new Exception("no original-table reread");
                }
                string auditPath=Path.ChangeExtension(journal,".clock.jsonl");
                if(!File.Exists(auditPath)) throw new Exception("clock audit missing");
                var audit=File.ReadLines(auditPath).Select(s=>System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(s)).ToArray();
                string? Text(System.Text.Json.JsonElement a,string k)=>a.GetProperty(k).GetString();
                bool Null(System.Text.Json.JsonElement a,string k)=>a.GetProperty(k).ValueKind==System.Text.Json.JsonValueKind.Null;
                foreach(var a in audit) {
                    if(Text(a,"ClockSource")=="timeToThink_20" || Text(a,"ClockStatus")!="known" && !Null(a,"ClockSeconds"))
                        throw new Exception("clock audit used opponent/stale/unknown clock");
                    if(Text(a,"ClockStatus")=="known" && (Text(a,"ClockSource")!="timeToThink_10" || !Text(a,"ClockPhase")!.EndsWith("/10")))
                        throw new Exception("known clock lacks current own bound phase");
                    if(Null(a,"ClockReadAt")) throw new Exception("clock audit missing observation time");
                }
                if(mode is "unknown" or "invalid") {
                    if(audit.Any(a=>Text(a,"ClockStatus")!="unknown" || !Null(a,"ClockSeconds")) ||
                        !audit.Any(a=>Text(a,"WaitReason")=="interface-lock" && Text(a,"decision")=="reread-original-table" && a.GetProperty("waitMilliseconds").GetInt64()>=5000))
                        throw new Exception("unknown clock did not use bounded lock reread");
                }
                if(mode is "client" or "client-short") {
                    var client=audit.Where(a=>Text(a,"WaitReason")=="client-state").ToArray();
                    if(client.Length==0 || client.Any(a=>!Null(a,"ClockSeconds")||!Null(a,"ClockSource")))
                        throw new Exception("client state published leftover DOM clock");
                    if(mode=="client-short" && !client.Any(a=>Text(a,"decision")=="reread-original-table" &&
                        a.GetProperty("budgetMilliseconds").GetInt64()<5000 && !Null(a,"verifiedDeadlineRemainingMs")))
                        throw new Exception("known pre-submit deadline lost in client state");
                }
                if(mode is "client-opponent" or "client-not-realtime") {
                    string expected=mode=="client-opponent"?"not-own-turn":"not-realtime";
                    var waits=audit.Where(a=>Text(a,"WaitReason")=="client-state").ToArray();
                    if(waits.Length==0 || waits.Any(a=>Text(a,"ClockStatus")!=expected || !Null(a,"ClockSeconds") ||
                        !Null(a,"ClockSource") || !Null(a,"verifiedDeadlineRemainingMs")) ||
                        !waits.Any(a=>Text(a,"decision")=="reread-original-table" && a.GetProperty("waitMilliseconds").GetInt64()>=5000))
                        throw new Exception("definite non-own/non-realtime state inherited prior deadline");
                }
                if(mode is "opponent" or "animation") {
                    if(audit.Any(a=>Text(a,"ClockStatus")!="known"||a.GetProperty("ClockSeconds").GetInt32()!=80) ||
                        audit.Any(a=>Text(a,"decision")=="reread-original-table")) throw new Exception("opponent clock/short animation misclassified");
                }
                if(mode=="cross") {
                    var waits=audit.Where(a=>Text(a,"WaitReason")=="interface-lock").ToArray();
                    if(!waits.Any(a=>a.GetProperty("ClockSeconds").GetInt32()==11) ||
                        !waits.Any(a=>a.GetProperty("ClockSeconds").GetInt32()==10) ||
                        waits.Last().GetProperty("waitMilliseconds").GetInt64()<=waits[1].GetProperty("waitMilliseconds").GetInt64())
                        throw new Exception("clock threshold crossing reset the wait");
                }
                Log($"AUDIT verified rows={audit.Length} path={auditPath}");
                Log($"PASS accepted={accepted} duplicates={duplicates} loads={loads} unresolved={unresolved} remaining={secondRemaining}ms playing={desk.IsPlaying}");
            }
            catch(Exception ex){error=ex;Log($"FAIL {ex.Message} accepted={accepted} duplicates={duplicates} expired={expired} loads={loads} elapsed-after-first={started.ElapsedMilliseconds-firstAccepted}ms");}
            finally{desk.Close();}
        };
        Application.Run(desk);
        Log("evidence="+root);
        return error==null?0:1;
    }
}
