using System.Text.Json;

internal static class Fixture
{
    internal static readonly string[] Names=["ordinary","first","next","next-second","idle","other","cooldown","security","active","wrong-account","one-seat","duplicate","bad-control","foreign-write",
        "dialog-ok","dialog-account","dialog-multiple","cool-absolute","cool-relative","cool-repeat","cool-no-time","cool-unready","cool-restored","verify-ok","verify-complete","verify-foreign","verify-device",
        "delayed-lobby","delayed-dialog","delayed-unknown","unhydrated","xhr-lobby","xhr-later","xhr-modal","xhr-queued","xhr-unknown"];
    private static DateTimeOffset? unlock;
    // 此入口截获所有请求；没有任何请求可从测试 profile 发往真实域名。
    internal static string? Response(string name,Uri uri,ref int docs,ref int requests)
    {
        if(uri.AbsolutePath is ("/lobby/lobby/getGamePreferences.html" or "/fixture/readonly-lobby-data")&&name.StartsWith("xhr-"))return "{\"loaded\":true}";
        if(uri.AbsolutePath=="/table/table/tableinfos.html"){
            var players=new Dictionary<string,object>{{"99769903",new {}}};
            if(name!="one-seat")players["20202"]=new {};
            return JsonSerializer.Serialize(new {id=name=="next-second"?"812002":"812001",game_name="seasaltpaper",status="archive",players});
        }
        if(uri.AbsolutePath=="/welcome")return "<script>window.globalUserInfos={id:'99769903',name:'bke780'}</script>";
        if(uri.AbsolutePath=="/security/verify"&&name=="verify-ok")
            return "<script>window.globalUserInfos={id:'99769903',name:'bke780'}</script><main><h2>身份验证</h2><p>请填写收到的一次性码</p><input autocomplete='one-time-code'></main>";
        if(uri.AbsolutePath=="/security/verify"&&name=="verify-complete")
            return "<script>window.globalUserInfos={id:'99769903',name:'bke780'}</script><main><h2>身份验证已完成</h2><p>验证已通过</p></main>";
        if(uri.AbsolutePath!="/lobby")return null;
        docs++;
        if(name.StartsWith("xhr-"))return $$"""
            <!doctype html><meta charset=utf-8><body><div id=area>加载中</div><script>
            const scenario={{JsonSerializer.Serialize(name)}};
            window.globalUserInfos={id:'99769903',name:'bke780'};
            fetch((scenario==='xhr-later'?'/fixture/readonly-lobby-data':'/lobby/lobby/getGamePreferences.html')+'?fixture=1').then(r=>r.ok?r.json():{}).then(s=>{
                if(!s.loaded)return;
                setTimeout(()=>{area.innerHTML='<div id=gamelobby-module class="rankingmode_simple gamemode_realtime opponentmode_automatic"><button id=wannaplay_1631>海盐折纸 开始游戏</button></div><div id=tournament_inprogress_banners style="min-height:1px"> </div><footer><h3>帮助</h3><h3>贡献</h3><h3>导航</h3><h3>关注我们</h3></footer>'+
                  (scenario==='xhr-modal'?'<div class=standard_popin_underlay></div><div class=standard_popin>另有通知</div>':'')+
                  (scenario==='xhr-queued'?'<div class=wannaplay_waiting>正在匹配</div>':'')+
                  (scenario==='xhr-unknown'?'<section role=dialog>未知业务<button>继续</button></section>':'');},
                  scenario==='xhr-later'?6200:400);
            });</script>
            """;
        if(name.StartsWith("delayed-")||name=="unhydrated"){
            return $$"""
            <!doctype html><meta charset=utf-8><body><div id=placeholder>载入中</div><script>
            const mode={{JsonSerializer.Serialize(name)}};
            window.globalUserInfos={id:'99769903',name:'bke780'};
            if(mode!=='unhydrated')setTimeout(()=>{
                if(mode==='delayed-lobby')placeholder.innerHTML='<div id=gamelobby-module class="rankingmode_simple gamemode_realtime opponentmode_automatic">普通即时制大厅</div>';
                else {
                    placeholder.innerHTML='<section role=dialog><h2>平台通知</h2><p>'+(mode==='delayed-dialog'?'新功能公告已发布。':'请确认切换账号。')+'</p><button>知道了</button></section>';
                    document.querySelector('section button').onclick=()=>{chrome.webview.postMessage('fixture-ack');document.querySelector('section').remove();
                        placeholder.innerHTML='<div id=gamelobby-module class="rankingmode_simple gamemode_realtime opponentmode_automatic"><button id=wannaplay_1631>海盐折纸 开始游戏</button></div>';};
                }
            },2500);
            if(mode==='delayed-lobby')setTimeout(()=>{document.querySelector('#gamelobby-module').innerHTML='<button id=wannaplay_1631>海盐折纸 开始游戏</button>';},4200);
            </script>
            """;
        }
        if(name.StartsWith("dialog-")||name.StartsWith("cool-")||name.StartsWith("verify-")){
            bool cool=name.StartsWith("cool-"),auth=name.StartsWith("verify-");
            unlock??=DateTimeOffset.UtcNow.AddSeconds(5);
            bool cleared=name=="cool-restored"||cool&&docs>1&&DateTimeOffset.UtcNow>=unlock&&name!="cool-repeat";
            string msg=name switch {
                "dialog-ok" or "dialog-multiple"=>"<p>平台新功能公告已发布。</p><button>知道了</button>"+(name=="dialog-multiple"?"<button>继续</button>":""),
                "dialog-account"=>"<p>请确认切换账号。</p><button>确认</button>",
                "cool-relative"=>"<p>请等待 5 秒</p><button>知道了</button>",
                "cool-no-time"=>"<p>暂时无法匹配</p><button>确认</button>",
                "verify-device"=>"<p>请在手机上批准</p>",
                "verify-foreign"=>"<p>进入验证流程</p><a href='https://unknown.invalid/security'>开始验证</a>",
                "verify-ok" or "verify-complete"=>"<p>进入官方验证流程</p><a href='/security/verify'>开始验证</a>",
                _=>"<p>官方解禁时间 <time datetime='"+(docs>1?DateTimeOffset.UtcNow.AddMinutes(1):unlock.Value).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ")+"'>解禁时间</time></p><button>确认</button>"
            };
            return "<meta charset=utf-8><div id=gamelobby-module class='rankingmode_simple gamemode_realtime opponentmode_automatic'>普通即时制大厅</div>"+
                (name=="cool-unready"?"":"<button id=wannaplay_1631>海盐折纸 开始游戏</button>")+
                (cleared?"":"<section role=dialog><h2>"+(cool?"匹配冷却":auth?"安全验证":"平台通知")+"</h2>"+msg+"</section>")+
                "<script>window.globalUserInfos={id:'99769903',name:'bke780'};document.querySelector('section button,section a')?.addEventListener('click',e=>{chrome.webview.postMessage('fixture-ack');if(e.target.tagName==='BUTTON')document.querySelector('section').remove()})</script>";
        }
        string data=JsonSerializer.Serialize(name);
        return $$"""
        <!doctype html><meta charset=utf-8><body><div id="gamelobby-module" class="rankingmode_simple gamemode_realtime opponentmode_automatic">普通即时制大厅</div><button id="wannaplay_1631">海盐折纸 开始游戏</button>
        <script>
        const scenario={{data}};
        window.globalUserInfos={id:scenario==='wrong-account'?'33':'99769903',name:'bke780'};
        window.mainsite={ajaxcall:(url,p,owner,done,err)=>fetch(url+'?id='+p.id).then(r=>r.json()).then(done).catch(()=>err(true))};
        function render(id){
          const extra={other:'<p>其他提示</p><button>继续</button>',cooldown:'<p>冷却：请等待至2026-12-05 18:00</p>',
            security:'<p>需要本人设备验证码</p>',active:'<div class=gamestart_overlay>匹配到对手</div>'}[scenario]||'';
          document.body.innerHTML='<h1>糟糕，看来你未能完成某一场游戏 :(</h1><p><a href="/table?table='+id+'">海盐折纸</a> (2026年Nov4日 15:45)</p>'+
            '<p>'+(scenario==='first'?'别担心：既然这是第一次，您的名誉不会受到任何惩罚（☯）。':'名誉值惩罚: -20 ☯ （因为你近期又受到了另一项惩罚）')+
            '</p><p>对每个人来说，未完成的对局视为败局。</p>'+extra+'<a id=ack href="'+(scenario==='bad-control'?'/lobby':'/welcome')+'">明白了！</a>'+
            (scenario==='duplicate'?'<a href="/welcome">明白了！</a>':'');
          ack.onclick=e=>{e.preventDefault();chrome.webview.postMessage('fixture-ack');
            if(scenario==='next'){render('812002');return;}
            history.pushState({},'','/welcome');document.body.innerHTML='<p>欢迎</p>';};
        }
        if(scenario!=='idle')setTimeout(()=>{history.pushState({},'','/penalty');render(scenario==='next-second'?'812002':'812001');},420);
        if(scenario==='foreign-write'){fetch('/lobby/queue/join.html');fetch('/act',{method:'POST'});}
        </script>
        """;
    }
}
