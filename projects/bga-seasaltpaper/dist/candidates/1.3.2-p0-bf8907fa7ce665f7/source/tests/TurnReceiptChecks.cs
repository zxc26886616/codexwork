using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class TurnReceiptChecks
{
    public static int Run(string mode, string? seed = null, string? ledger = null)
    {
        bool discard = mode is "discard1" or "discard2" or "discard-stuck" or "discard-same";
        bool pick = mode is "pick" or "pick-stuck" or "pick-same" or "pick-no-board" or "pick-wrong-actor" or "pick-lost-actor";
        bool score = mode is "score-next" or "score-stuck" or "score-same" or "score-again" or "score-wrong";
        if (!discard && !pick && !score && mode is not ("turn" or "turn-equal" or "draw" or "same" or "stuck" or "client" or "no-move" or "wrong-actor" or "missing-state" or "reopen")) throw new ArgumentException(nameof(mode));
        ApplicationConfiguration.Initialize();
        var root = Directory.CreateTempSubdirectory("SeaSalt-turn-receipt-").FullName;
        var url = new Uri("https://fixture.studio.boardgamearena.com/1/seasaltpaper?table=812");
        string intentPath = Path.Combine(root, "seat-actions", "Studio-812-10.json");
        var elapsed = Stopwatch.StartNew();
        void Log(string line) {
            line = $"{elapsed.Elapsed.TotalSeconds:F3}s {line}"; Console.WriteLine(line);
            File.AppendAllText(Path.Combine(root, "server.txt"), line + Environment.NewLine);
        }
        bool turn = mode is "turn" or "turn-equal" or "client" or "same" or "wrong-actor" or "missing-state";
        bool positive = mode is "turn" or "turn-equal" or "draw" or "discard1" or "discard2" or "pick" or "score-next";
        string firstAction = score ? "seen" : pick ? "keep" : discard ? (mode=="discard2"?"discard2":"discard1") : turn ? "end" : "draw";
        string secondAction = score ? "draw" : pick ? "discard1" : discard ? "end" : turn ? "draw" : "keep";
        int docs=0, attempts=0, accepted=0, incidents=0, staleDispatch=0;
        string? firstId=null, original=null;
        int proofsBeforeNavigation=0;
        long? projectionStarted=null, strongAt=null;
        using var desk = new Desk(root,url,true);
        var module=typeof(Desk).Assembly;
        Log($"mode={mode} root={root} sha={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(module.Location)))} mvid={module.ManifestModule.ModuleVersionId}");
        if(mode=="reopen"){
            if(seed==null || ledger is not ("missing" or "existing"))throw new ArgumentException(nameof(seed));
            Directory.CreateDirectory(Path.GetDirectoryName(intentPath)!);
            File.Copy(Path.Combine(seed,"captured-handoff.json"),intentPath);
            original=File.ReadAllText(intentPath);
            firstId=JsonDocument.Parse(original).RootElement.GetProperty("id").GetString();
            if(ledger=="existing")File.Copy(Path.Combine(seed,"captured-progress.jsonl"),Path.ChangeExtension(intentPath,".progress.jsonl"));
            Log($"new process reopening intent={firstId} ledger={ledger} before={Proofs()}");
        }
        desk.LogLine += s=>{if(s.StartsWith("原桌官方阶段已切换"))projectionStarted??=elapsed.ElapsedMilliseconds;Log("desk "+s);};
        desk.MatchedReceiptUnresolved += (_,id)=> {incidents++;strongAt=elapsed.ElapsedMilliseconds;Log("strong "+id);};
        int Proofs() {
            var path=Path.ChangeExtension(intentPath,".progress.jsonl");
            return File.Exists(path) ? File.ReadLines(path).Count(s=>JsonDocument.Parse(s).RootElement.GetProperty("Id").GetString()==firstId):0;
        }
        desk.GameBrowser.CoreWebView2InitializationCompleted += (_, init) => {
            if (!init.IsSuccess) return;
            var core=desk.GameBrowser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, request) => {
                var uri=new Uri(request.Request.Uri);
                string? body=null;
                if(uri==url) {
                    docs++;
                    bool originalState=docs==1 || mode is "same" or "client" or "missing-state";
                    string state=originalState ? (turn?"playCards":"takeCards") : (turn?"takeCards":"chooseCard");
                    if(discard) state=docs==1 || mode=="discard-same" ? "putDiscardPile" : "playCards";
                    if(pick) state=docs==1 || mode=="pick-same" ? "chooseCard" : "putDiscardPile";
                    if(score) state=docs==1 || mode is "score-same" or "score-again" ? "beforeEndRound" : "takeCards";
                    bool oldFrame=docs==1;
                    Log($"GET original doc={docs} state={state} accepted={accepted}");
                    body=$$$$$"""
                    <!doctype html><meta charset=utf-8><style>.card,button,#deck,#discard1,#discard2{width:45px;height:30px}</style>
                    <span id=move_nbr>237</span><span id=timeToThink_10>01:46</span>
                    <div id=deck>抽牌</div><div id=discard1></div><div id=discard2></div>
                    <div class=player-table><div data-my-hand=true data-player-id=10><div class=card id=hand1>牌</div></div></div>
                    <div id=pick><div class=card id=pick1>保留</div></div>
                    <div id=generalactions><button>结束回合</button></div>
                    <script>
                    const mode='{{{{{mode}}}}}',old={{{{{oldFrame.ToString().ToLowerInvariant()}}}}};
                    window.bgaGameData={isRealTime:true};
                    window.gameui={player_id:10,table_id:812,game_name:"seasaltpaper",bRealtime:true,gamedatas:{players:{10:{},20:{}}}};
                    const game=window.gameui; let locked=false,reads=0,synced=old;
                    function state(name,who) {
                      const s={id:{takeCards:20,chooseCard:30,putDiscardPile:35,playCards:40,beforeEndRound:79}[name],name,type:'activeplayer',active_player:who,args:{canTakeFromDeck:true,canTakeFromDiscard:[]}};
                      if(name==='beforeEndRound'){s.type='multipleactiveplayer';s.multiactive=[10,20];}
                      game.gamedatas.gamestate=s;game.last_server_state=JSON.parse(JSON.stringify(s));
                    }
                    state('{{{{{state}}}}}',10);game.isCurrentPlayerActive=()=>game.gamedatas.gamestate.active_player===10;
                    game.isInterfaceLocked=()=>locked;
                    const discard=mode.startsWith('discard'), picking=mode.startsWith('pick'), chosen=pick1;
                    let observedMove=picking?72:discard?113:237;
                    const score=mode.startsWith('score-');
                    if(score){
                      observedMove=287;move_nbr.textContent='287';timeToThink_10.textContent='01:30';
                      document.querySelector('button').textContent='我确认完了';
                      if(!old && mode==='score-again')state('beforeEndRound',20),game.isCurrentPlayerActive=()=>true;
                      if(!old && mode!=='score-same')document.querySelector('[data-my-hand]').appendChild(chosen);
                      if(!old && mode==='score-wrong')game.last_server_state.active_player=99;
                    }
                    if(picking){
                      chosen.id='ssp-card-39';const other=chosen.cloneNode(true);other.id='ssp-card-12';pick.appendChild(other);
                      move_nbr.textContent='72';timeToThink_10.textContent=old?'01:22':'01:14';
                      if(!old && mode!=='pick-same' && mode!=='pick-no-board')document.querySelector('[data-my-hand]').appendChild(chosen);
                      discard1.onclick=()=>send('discard1');discard2.onclick=()=>send('discard2');
                      if(!old && mode==='pick-wrong-actor')game.last_server_state.active_player=99;
                    }
                    if(discard){
                      move_nbr.textContent='113';timeToThink_10.textContent=old?'01:11':'01:03';
                      if(mode==='discard2')discard1.innerHTML='<div class="card" id="existing">旧明牌</div>';
                      if(!old&&mode!=='discard-same')(mode==='discard2'?discard2:discard1).appendChild(pick1);
                      discard1.onclick=()=>send('discard1');discard2.onclick=()=>send('discard2');
                    }
                    if(!old){
                      if(mode==='wrong-actor')game.last_server_state.active_player=99;
                      Object.defineProperty(move_nbr,'textContent',{get(){
                        reads++;
                        if(score){
                          observedMove=mode==='score-same'?287:mode==='score-again'?288:mode!=='score-next'||reads===1?249:289;
                          synced=observedMove===289;return String(observedMove);
                        }
                        if(picking){
                          observedMove=mode==='pick-same'?72:mode==='pick-stuck'||mode==='pick-wrong-actor'||mode==='pick-no-board'||reads===1?51:73;
                          synced=observedMove===73;
                          if(mode==='pick-lost-actor'&&reads===1)setTimeout(()=>game.last_server_state.active_player=99,100);
                          return String(observedMove);
                        }
                        if(discard){
                          observedMove=mode==='discard-same'?113:mode==='discard-stuck'||reads===1?47:114;
                          synced=observedMove===114;
                          return String(observedMove);
                        }
                        if(mode==='same'||mode==='client'||mode==='missing-state')return '237';
                        if(mode==='no-move')return '—';
                        if(mode==='stuck'||reads===1||mode==='turn-equal'&&reads===2)return '1';
                        synced=true;return '245';
                      }});
                    }
                    function send(action){
                      const r=new XMLHttpRequest();r.open('POST','/act?action='+action+'&synced='+synced+'&reads='+reads+'&move='+observedMove,false);r.send();
                      if(old && action==='end' && mode!=='same'){
                        move_nbr.textContent='238';state('takeCards',20);
                        if(mode==='client')game.on_client_state=true;
                        if(mode==='missing-state')delete game.last_server_state;
                        if(mode==='turn-equal')setTimeout(()=>{move_nbr.textContent='245';state('playCards',20);},1050);
                      }else locked=true;
                    }
                    document.querySelector('button').onclick=()=>send(score?'seen':'end');deck.onclick=()=>send('draw');chosen.onclick=()=>send('keep');
                    </script>
                    """;
                } else if(uri.AbsolutePath=="/act"){
                    var q=System.Web.HttpUtility.ParseQueryString(uri.Query); string? action=q["action"];
                    attempts++;
                    if(attempts==1){
                        original=File.ReadAllText(intentPath);firstId=JsonDocument.Parse(original).RootElement.GetProperty("id").GetString();
                        if(mode is not ("same" or "discard-same" or "pick-same" or "score-same") && action==firstAction &&
                            (!discard || q["move"]=="113") && (!pick || q["move"]=="72") && (!score || q["move"]=="287"))accepted++;
                    } else if(positive && accepted==1 && action==secondAction && q["synced"]=="true" &&
                        (!discard || q["move"]=="114") && (!pick || q["move"]=="73") && (!score || q["move"]=="289")) accepted++;
                    else staleDispatch++;
                    Log($"POST action={action} attempts={attempts} accepted={accepted} first={firstId} synced={q["synced"]} reads={q["reads"]} move={q["move"]}");
                    body="{}";
                }
                request.Response=core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(body??"")),body==null?404:200,body==null?"Not Found":"OK","Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
            };
        };
        Exception? failure=null;
        desk.Shown+=async (_,_)=>{
            try{
                var until=DateTime.UtcNow.AddSeconds(12);
                while(!desk.StartMatchedSeat("Studio","812","10","20")) {if(DateTime.UtcNow>=until)throw new TimeoutException("arm");await Task.Delay(100);}
                if(mode=="reopen"){
                    var entry=JsonDocument.Parse(File.ReadAllLines(Path.ChangeExtension(intentPath,".progress.jsonl")).Single()).RootElement;
                    if(Proofs()!=1 || entry.GetProperty("evidence").GetString()!="opponent-turn" ||
                        File.ReadAllText(intentPath)!=original || desk.MatchedConfirmedActions!=0 || attempts!=0)
                        throw new Exception("reopen failed to repair/deduplicate the old evidence without claiming a new action");
                    Log($"PASS original={firstId} proofs={Proofs()} newConfirmed={desk.MatchedConfirmedActions} attempts={attempts}");return;
                }
                while(attempts==0) {if(DateTime.UtcNow>=until)throw new TimeoutException("first");await Task.Delay(100);}
                await Task.Delay(2300);
                proofsBeforeNavigation=Proofs();
                var pendingBefore=JsonDocument.Parse(File.ReadAllText(intentPath)).RootElement;
                long? floor=pendingBefore.TryGetProperty("opponentMove",out var sampled) && sampled.ValueKind==JsonValueKind.Number && sampled.TryGetInt64(out var sample)?sample:null;
                Log($"before reload progress={proofsBeforeNavigation} opponentMove={floor}");
                if(mode=="turn-equal" && floor!=245)throw new Exception("fixture did not see highest opponent move 245");
                if(mode=="turn-equal"){
                    File.Copy(intentPath,Path.Combine(root,"captured-handoff.json"));
                    File.Copy(Path.ChangeExtension(intentPath,".progress.jsonl"),Path.Combine(root,"captured-progress.jsonl"));
                }
                if(!discard && !pick && !score)desk.GameBrowser.CoreWebView2.Reload();
                until=DateTime.UtcNow.AddSeconds(discard||pick||score?15:7);
                while((positive ? accepted<2 : incidents==0) && DateTime.UtcNow<until)await Task.Delay(60);
                await Task.Delay(180);
                int proofs=Proofs();
                Log($"result attempts={attempts} accepted={accepted} incidents={incidents} firstId={firstId} proofs={proofs} beforeReload={proofsBeforeNavigation} staleDispatch={staleDispatch} playing={desk.IsPlaying}");
                if(mode=="wrong-actor" ? !(incidents==1 && proofs==1 && attempts==1 && proofsBeforeNavigation==1) :
                    positive ? !(accepted==2 && attempts==2 && incidents==0 && proofs==1 && proofsBeforeNavigation==(turn?1:0)) :
                    !(incidents==1 && proofs==0 && attempts==1 && File.ReadAllText(intentPath)==original))
                    throw new Exception("receipt evidence or stop/dispatch invariant failed");
                if(!desk.IsPlaying || staleDispatch!=0)throw new Exception("seat or dispatch violated");
                if(mode is "stuck" or "no-move" or "discard-stuck" or "pick-stuck" or "score-stuck" && !(strongAt-projectionStarted is >=2450 and <=4500))
                    throw new Exception("inconsistent projection was not bounded from its first observation");
                if(mode is "same" or "client" or "missing-state" or "discard-same" or "pick-same" or "pick-no-board" or "pick-wrong-actor" or "score-same" or "score-again" or "score-wrong" && projectionStarted!=null)
                    throw new Exception("a complete unchanged state must stop at the first fresh observation");
                if(discard && (docs<2 || (mode=="discard-same" && accepted!=0) || (positive && projectionStarted==null)))
                    throw new Exception("discard did not exercise the original-table reconciliation path");
                if(pick && (docs<2 || accepted!=(mode=="pick-same"?0:positive?2:1) || positive && projectionStarted==null))
                    throw new Exception("selection did not exercise the original-table reconciliation path");
                if(score && (docs<2 || accepted!=(mode=="score-same"?0:positive?2:1) || positive && projectionStarted==null))
                    throw new Exception("score receipt did not exercise the original-table reconciliation path");
                if(score && (desk.UnresolvedScoreIntent==firstId)!=(mode=="score-same"))
                    throw new Exception("score hold was not limited to the original pending confirmation");
                Log("PASS");
            }catch(Exception ex){failure=ex;Log("FAIL "+ex.Message);}finally{desk.Close();}
        };
        Application.Run(desk);return failure==null?0:1;
    }
}
