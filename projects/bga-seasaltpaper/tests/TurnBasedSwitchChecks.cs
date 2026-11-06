extern alias SeatWorker;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;
using Switch = SeatWorker::TurnBasedSwitch;

internal static class TurnBasedSwitchChecks
{
    private const string Origin = "https://switch-fixture.boardgamearena.com";
    private const string Shell = """
        <!doctype html><script>
        window.globalUserInfos={id:10};window.moves=[];window.sides=[];
        window.propose=id=>{
            moves.push('propose'+id);
            for(const side of sides){
                const d=side.document;
                d.getElementById('table-decision').hidden=false;
                d.getElementById('decision-title').textContent='转为回合制？ (2)';
                d.querySelector('#decision-players-undecided b').textContent='房主';
            }
        };
        window.agree=id=>{moves.push('agree'+id);setTimeout(()=>{for(const s of sides)s.turnBased=true},1300)};
        </script><iframe id="gameIframe" src="/3/seasaltpaper?table=900"></iframe>
        """;
    private const string Game = """
        <!doctype html><meta charset="utf-8">
        <button id="ingame_menu">菜单</button><button id="ingame_menu_switch_tb" hidden>切换到回合制</button>
        <div id="table-decision" hidden><span id="decision-title"></span>
            <div id="decision-players-undecided"><b></b></div><button id="decision_yes">是</button></div>
        <script>
        const id=new URLSearchParams(location.search).has('testuser')?20:10, el=id=>document.getElementById(id);
        top.sides.push(window);window.turnBased=false;window.__=()=> '转为回合制？';
        window.gameui={game_name:'seasaltpaper',table_id:900,player_id:id,hotseat_interface:id===10?'normal':'hotseataccount',
            players_metadata:{20:{type:'hotseat'}}, hotseatplayers:[10,20],bgaInternal:{hotseat:{hotseat_focus:20}},
            bga:{players:{getCurrentPlayerId:()=>id,isCurrentPlayerSpectator:()=>false},tableOptions:{isTurnBased:()=>window.turnBased}},
            gamedatas:{players:{10:{name:'房主'},20:{name:'客席'}},gamestate:{name:'chooseCard'},decision:{decision_type:'none'}}};
        el('ingame_menu').onclick=()=>{top.moves.push('menu'+id);el('ingame_menu_switch_tb').hidden=false};
        el('ingame_menu_switch_tb').onclick=()=>top.propose(id);
        el('decision_yes').onclick=()=>top.agree(id);
        if(id===10){
            document.body.insertAdjacentHTML('beforeend','<div id="hotseat_mask"><button id="hotseat_play_button">交接</button></div>'+
                '<iframe id="hotseat_iframe_20" src="/3/seasaltpaper?table=900&testuser=20"></iframe>');
            el('hotseat_play_button').onclick=()=>{top.moves.push('gate');setTimeout(()=>el('hotseat_mask').hidden=true,1300)};
        }
        </script>
        """;

    public static async Task Run(TableBrowser web)
    {
        var core = web.CoreWebView2;
        string filter = Origin + "/*";
        void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!e.Request.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
            string page = new Uri(e.Request.Uri).AbsolutePath switch { "/tableview" => Shell, "/3/seasaltpaper" => Game, _ => "" };
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(page)),
                page == "" ? 404 : 200, page == "" ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
        }
        core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Serve;
        try
        {
            core.Navigate(Origin + "/tableview?table=900");
            var until = DateTime.UtcNow.AddSeconds(15);
            while (await core.ExecuteScriptAsync("window.sides?.length") != "2")
            {
                if (DateTime.UtcNow >= until) throw new TimeoutException("转换回合制的受控牌桌未就绪。");
                await Task.Delay(80);
            }
            Task<bool> Convert() => Switch.TryConvert(web, "900", "10", "客席");
            if (await Switch.TryConvert(web, "900", "10", "错席") || await Switch.TryConvert(web, "901", "10", "客席"))
                throw new Exception("昵称或桌号错误仍允许转换回合制。");
            await core.ExecuteScriptAsync("sides[0].gameui.players_metadata[20].type='player'");
            if (await Convert()) throw new Exception("混有远程玩家的牌桌仍允许转换回合制。");
            await core.ExecuteScriptAsync("sides[0].gameui.players_metadata[20].type='hotseat';" +
                "sides[1].document.getElementById('table-decision').hidden=false;" +
                "sides[1].document.getElementById('decision-title').textContent='共同弃局？'");
            if (await Convert() || await core.ExecuteScriptAsync("moves.length") != "0")
                throw new Exception("误操作了其他投票或席位。");
            await core.ExecuteScriptAsync("sides[1].document.getElementById('table-decision').hidden=true");
            if (!await Convert() || !await Convert() ||
                await core.ExecuteScriptAsync("moves") != """["gate","menu20","propose20","agree10"]""")
                throw new Exception("热座转回合制未收到双方确认，或重复点击了提议与同意。");
            Console.WriteLine("通过 · 热座同桌转回合制、跨席同意、延迟回执去重、拒绝其他桌与其他投票。");
        }
        finally
        {
            core.WebResourceRequested -= Serve;
            core.RemoveWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
        }
    }
}
