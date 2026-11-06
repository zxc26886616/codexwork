using System.Text;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class HotseatChecks
{
    private const string Origin = "https://hotseat-fixture.studio.boardgamearena.com";
    private const string Shell = """
        <!doctype html><script>window.moves=[]</script>
        <iframe id="gameIframe" src="/1/seasaltpaper?table=78"></iframe>
        """;
    private const string SeatPage = """
        <!doctype html><meta charset="utf-8"><div id="deck"></div><div id="hand" data-my-hand="true"></div>
        <div id="generalactions"><button id="seen_button">已阅</button></div>
        <script>
        const id = Number(new URLSearchParams(location.search).get("testuser") || 10);
        document.getElementById("hand").dataset.playerId=id;
        document.getElementById("seen_button").onclick=()=>top.moves.push(id);
        window.gameui={
            game_name:"seasaltpaper",table_id:78,player_id:id,hotseat_interface:id===10?"normal":"hotseataccount",
            hotseatplayers:[10,20],players_metadata:{10:{type:"player"},20:{type:"hotseat"}},
            gamedatas:{players:{10:{},20:{}},gamestate:{name:"beforeEndRound",type:"multipleactiveplayer"}},
            bgaInternal:{hotseat:{hotseat_focus:20}},
            bga:{players:{getCurrentPlayerId:()=>id,isCurrentPlayerActive:()=>true,
                isCurrentPlayerSpectator:()=>false,getActivePlayerIds:()=>[10,20]}}
        };
        if (id===10) {
            document.body.insertAdjacentHTML("beforeend",'<div id="hotseat_mask"><button id="hotseat_play_button">交接</button></div>' +
                '<iframe id="hotseat_iframe_20" src="/1/seasaltpaper?table=78&testuser=20"></iframe>');
            // 让蒙版再停留一次轮询，模拟站点还没确认上一击。
            document.getElementById("hotseat_play_button").onclick=()=>top.moves.push(0);
        }
        </script>
        """;

    public static async Task Run(TableBrowser browser)
    {
        var core = browser.CoreWebView2;
        string filter = Origin + "/*";
        void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (!e.Request.Uri.StartsWith(Origin + "/", StringComparison.Ordinal)) return;
            string? page = new Uri(e.Request.Uri).AbsolutePath switch
            {
                "/tableview" => Shell, "/1/seasaltpaper" => SeatPage, _ => null
            };
            e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(page ?? "")),
                page == null ? 404 : 200, page == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
        }
        core.AddWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += Serve;
        try
        {
            core.Navigate(Origin + "/tableview?table=78");
            const string group = "Studio/78/热座/10/10-20", single = "Studio/78/座位/10";
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (await browser.ReadAsync() is not { Kind: "ready", Table: group })
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("双层热座桌未就绪。");
                await Task.Delay(50);
            }
            await Check(single, "ready", "[]", "单座授权误触了交接按钮。");
            await Check(group, "action", "[0]", "热座交接时提前操作了手牌。");
            await Check(group, "wait", "[0]", "同一次热座交接重复点击。");
            const string host = "document.getElementById('gameIframe').contentWindow";
            await core.ExecuteScriptAsync(host + ".document.getElementById('hotseat_mask').style.display='none'");
            await Check(group, "action", "[0,20]", "热座未在当前客席确认记分。");
            await core.ExecuteScriptAsync(host + ".gameui.bgaInternal.hotseat.hotseat_focus=10");
            await Check(group, "action", "[0,20,10]", "焦点切回主持席后未在该席确认记分。");
            await core.ExecuteScriptAsync(host + ".gameui.bgaInternal.hotseat.hotseat_focus=20;" +
                host + ".gameui.players_metadata[20].type='player';" +
                host + ".document.getElementById('hotseat_mask').style.display='block'");
            if ((await Check(group, "wait", "[0,20,10]", "混合桌误接管了远程席位。")).Table != single)
                throw new Exception("混合桌仍在使用全桌热座授权。");
            Console.WriteLine("通过 · 双层热座、单座授权隔离、交接去重、双席记分与远程混桌保护。");
        }
        finally
        {
            core.WebResourceRequested -= Serve;
            core.RemoveWebResourceRequestedFilter(filter, CoreWebView2WebResourceContext.All);
        }

        async Task<Reading> Check(string key, string kind, string moves, string failure)
        {
            var reading = await browser.ReadAsync(key);
            if (reading.Kind != kind || await core.ExecuteScriptAsync("window.moves") != moves)
                throw new Exception(failure);
            return reading;
        }
    }
}
