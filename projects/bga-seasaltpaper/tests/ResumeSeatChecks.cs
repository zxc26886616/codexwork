extern alias SeatWorker;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class ResumeSeatChecks
{
    public static int Run(string mode)
    {
        if (mode is not ("advance" or "opponent" or "handoff-equal" or "same" or "same-later-move" or "no-clock" or "wrong" or "no-intent" or "ended" or "lease")) throw new ArgumentException();
        string root=Directory.CreateTempSubdirectory("SeaSalt-resume-seat-").FullName, batch=Guid.NewGuid().ToString("N");
        var book=new SeriesBook(root);book.Start(3,"10","",batch,"Studio");book.Queue(batch,true);
        book.AcceptOffer(batch,"991","20");book.Attach(batch,"991","20");
        string journal=Path.Combine(root,"seat-actions","Studio-991-10.json");
        Directory.CreateDirectory(Path.GetDirectoryName(journal)!);
        const string priorId="99770000111122223333444455556666";
        string original=JsonSerializer.Serialize(new {id=priorId, table="Studio/991/座位/10", opponent="20",generation=22,
            plan=new SeatPlan("prior-process-document","40/playCards/10",25,"[[\"held25\"],[],[],[],[],[]]","playCards|10|||结束回合","", "end-turn",true),
            phaseSource="last-server-state", opponentMove=mode=="handoff-equal"?(long?)27:null,
            firstHandoff=mode=="handoff-equal"?new OpponentTurn("prior-process-document","20/takeCards/20",27):null},MatchBook.Json);
        if(mode!="no-intent")File.WriteAllText(journal,original);
        var time=Stopwatch.StartNew();
        void Log(string s){s=$"{time.Elapsed.TotalSeconds:F3}s {s}";Console.WriteLine(s);File.AppendAllText(Path.Combine(root,"authority.txt"),s+Environment.NewLine);}
        var app=typeof(Desk).Assembly;Log($"mode={mode} root={root} batch={batch} sha={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(app.Location)))} mvid={app.ManifestModule.ModuleVersionId}");
        int authority=0, endedReads=0, gameViews=0, draw=0, repeat=0, joins=0;
        bool cutoff=false, sawBound=false, clockPassed=false, preserved=true;
        long startView=-1;
        const string boot="""<script>globalUserInfos={id:'10'};mainsite={ajaxcall:(u,a,o,success,done)=>fetch('/authority').then(r=>r.json()).then(q=>{success(q);done?.(false)}).catch(()=>done?.(true))};</script>""";
        void Configure(Desk desk) {
            desk.LogLine+=s=>Log("desk "+s);
            desk.GameBrowser.CoreWebView2InitializationCompleted+=(_,init)=>{
                if(!init.IsSuccess)return;var core=desk.GameBrowser.CoreWebView2;
                core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
                core.WebResourceRequested+=(_,e)=>{
                    var u=new Uri(e.Request.Uri);string? page=null;
                    if(u.AbsolutePath=="/lobby")page=boot+"<button id=join onclick=\"fetch('/join')\">join</button>";
                    if(u.AbsolutePath=="/table")page=boot+"<div class=tablewithgame_seasaltpaper><div id=gametable_box></div></div>";
                    if(u.AbsolutePath=="/authority"){
                        authority++;bool ended=mode=="ended"||draw>0;if(ended)endedReads++;
                        page=JsonSerializer.Serialize(new {id=991,game_name="seasaltpaper",status=ended?"finished":"play",cancelled=0,
                            result=new{endgame_reason=ended?"normal_end":""},players=new Dictionary<string,object>{{"10",new{}},{mode=="wrong"?"99":"20",new{}}},
                            options=new Dictionary<int,object>{{101,new{value=1}},{102,new{value=1}},{110,new{value=1}},{200,new{value=1}},{201,new{value=0}}}});
                        Log("authority "+(ended?"finished":"play"));
                    }
                    if(u.AbsolutePath=="/tableview"){gameViews++;page=boot+"<iframe id=gameIframe src='/1/seasaltpaper?table=991'></iframe>";}
                    if(u.AbsolutePath=="/1/seasaltpaper"){
                        startView=startView<0?time.ElapsedMilliseconds:startView;
                        if(draw==0 && mode!="no-intent")preserved &= File.ReadAllText(journal)==original;
                        bool same=mode is "same" or "same-later-move";
                        page=boot+$$$$$"""
                        <span id=move_nbr>{{{{{(mode=="same"?25:27)}}}}}</span><span id=timeToThink_10>{{{{{(mode=="no-clock"?"—":"00:18")}}}}}</span>
                        <div id=deck style="width:40px;height:40px">抽牌</div><div id=discard1></div><div id=discard2></div>
                        <div class=player-table><div data-my-hand=true data-player-id=10><div class=card style="height:20px" id="held{{{{{(mode=="same"?25:27)}}}}}">卡</div></div></div>
                        <div id=generalactions><button>结束回合</button></div><script>
                        bgaGameData={isRealTime:true};
                        const s={id:{{{{{(same?40:20)}}}}},name:'{{{{{(same?"playCards":"takeCards")}}}}}',type:'activeplayer',active_player:10,args:{canTakeFromDeck:true,canTakeFromDiscard:[]}};
                        gameui={player_id:10,table_id:991,game_name:'seasaltpaper',bRealtime:true,gamedatas:{players:{10:{},20:{}},gamestate:s},
                          last_server_state:structuredClone(s),isCurrentPlayerActive:()=>s.active_player===10};
                        if('{{{{{mode}}}}}'==='opponent') {
                          s.active_player=20;gameui.last_server_state=structuredClone(s);move_nbr.textContent='26';
                          setTimeout(()=>{s.active_player=10;gameui.last_server_state=structuredClone(s);move_nbr.textContent='27';},2300);
                        }
                        function send(kind){const x=new XMLHttpRequest();x.open('POST','/'+kind,false);x.send();
                          if(kind==='draw'){s.name='gameEnd';gameui.last_server_state=structuredClone(s);}}
                        deck.onclick=()=>send('draw');document.querySelector('button').onclick=()=>send('end');
                        </script>
                        """;
                    }
                    if(u.AbsolutePath=="/draw"){draw++;sawBound=desk.IsPlaying;clockPassed=File.Exists(Path.ChangeExtension(journal,".clock.jsonl"))&&File.ReadAllText(Path.ChangeExtension(journal,".clock.jsonl")).Contains("timeToThink_10");Log($"server draw={draw} armed={sawBound} clockAudit={clockPassed} priorPreserved={preserved} stop={book.QueueStopped(batch)}");page="{}";}
                    if(u.AbsolutePath=="/end"){repeat++;Log("REPEATED old end turn");page="{}";}
                    if(u.AbsolutePath=="/join"){joins++;page="{}";}
                    e.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(page??"")),page==null?404:200,page==null?"Not Found":"OK","Content-Type: text/html;charset=utf-8\r\nCache-Control:no-store");
                };
            };
            desk.Shown+=async(_,_)=>{
                while(!desk.IsDisposed && time.Elapsed.TotalSeconds<15 && (startView<0||time.ElapsedMilliseconds-startView<5200||draw>0))await Task.Delay(100);
                if(!desk.IsDisposed){cutoff=true;desk.Close();}
            };
        }
        int exit;
        using(var held=mode=="lease"?File.Open(Path.Combine(root,"series-run.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None):null)
            exit=SeatWorker::SeriesRunner.MainSeries(["--root",root,"--resume",batch],Configure);
        var saved=book.Load(batch);
        string incidents=Path.ChangeExtension(book.PathFor(batch),".incidents.jsonl");
        string events=File.Exists(incidents)?File.ReadAllText(incidents):"";
        bool heldIntent=mode=="no-intent"?!File.Exists(journal):File.ReadAllText(journal)==original;
        bool ok=mode=="lease"?exit==3&&authority==0:book.QueueStopped(batch)&&joins==0&&repeat==0&&saved.Completed==0&&saved.CurrentTable=="991"&&
            (mode is "advance" or "opponent" or "handoff-equal"?exit==4&&draw==1&&sawBound&&clockPassed&&preserved&&endedReads>0&&!cutoff:
             mode=="ended"?draw==0&&exit==4&&endedReads>0&&gameViews==0&&heldIntent:
             mode=="wrong"?draw==0&&gameViews==0&&heldIntent&&!cutoff:
             draw==0&&gameViews>0&&cutoff&&heldIntent&&events.Contains(mode=="no-intent"?"resume-seat-proof-unavailable":"receipt-unresolved-after-authoritative-read"));
        Log($"{(ok?"PASS":"FAIL")} exit={exit} joins={joins} draws={draw} oldEnd={repeat} officialReads={authority} ended={endedReads} cutoff={cutoff} confirmedQuota={saved.Completed} original={heldIntent} stopped={book.QueueStopped(batch)}");return ok?0:1;
    }
}
