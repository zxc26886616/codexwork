using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class BackgroundChecks
{
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    public static int Run()
    {
        ApplicationConfiguration.Initialize();
        string root = Directory.CreateTempSubdirectory("SeaSalt-background-").FullName;
        var url = new Uri("https://background.studio.boardgamearena.com/1/seasaltpaper?table=791");
        nint foreground = GetForegroundWindow();
        using var desk = new Desk(root, url, background: true);
        var rows = new List<object>();
        var messages = new List<string>();
        desk.LogLine += messages.Add;
        Rectangle bounds = desk.Bounds;
        int handoverLoads = 0, exit = 1;
        desk.GameBrowser.CoreWebView2InitializationCompleted += (_, result) =>
        {
            if (!result.IsSuccess) return;
            var core = desk.GameBrowser.CoreWebView2;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                var request = new Uri(e.Request.Uri);
                if (request.Host == url.Host && request.AbsolutePath == "/tableview") handoverLoads++;
                const string page = """
                    <!doctype html><meta charset=utf-8><div id=deck></div>
                    <div data-my-hand=true data-player-id=10></div>
                    <script>window.bgaGameData={isRealTime:true};window.gameui={player_id:"10",table_id:"791",
                    game_name:"seasaltpaper",isCurrentPlayerActive:()=>false,
                    gamedatas:{players:{"10":{},"20":{}},gamestate:{name:"takeCards",active_player:"20"}}};</script>
                    """;
                e.Response = core.Environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(page)),
                    200, "OK", "Content-Type: text/html; charset=utf-8");
            };
        };
        void Inspect(string stage)
        {
            bool noActivate = (GetWindowLongPtr(desk.Handle, -20).ToInt64() & 0x08000000) != 0;
            bool sameForeground = GetForegroundWindow() == foreground;
            rows.Add(new { stage, sameForeground, noActivate, desk.Opacity, desk.TopMost, desk.ShowInTaskbar,
                boundsUnchanged = desk.Bounds == bounds, desk.IsPlaying });
            if (!sameForeground || !noActivate || desk.Opacity != 0 || desk.TopMost || desk.ShowInTaskbar || desk.Bounds != bounds)
                throw new Exception("后台故障处理改变了前台或窗口显示属性。");
        }
        desk.Shown += async (_, _) =>
        {
            try
            {
                var status = Checks.Children(desk).OfType<Label>().Single(l => l.Name == "status");
                var until = DateTime.UtcNow.AddSeconds(15);
                while (!status.Text.Contains("牌桌已就绪") && DateTime.UtcNow < until) await Task.Delay(50);
                if (!desk.StartMatchedSeat("Studio", "791", "10", "20")) throw new Exception("受控桌未能绑定。");
                Inspect("armed");
                desk.ShowMatchedTable("受控后台故障提示");
                if (!desk.IsPlaying || !messages.Contains("受控后台故障提示") || status.Text != "受控后台故障提示")
                    throw new Exception("后台提示未保留出牌状态或没有写入状态与日志。");
                Inspect("fault-notice");
                desk.HandOverMatchedTable(new Uri(url, "/tableview?table=791"), "受控原桌核对");
                if (desk.IsPlaying || !messages.Contains("需要接管原桌 · 受控原桌核对"))
                    throw new Exception("接管没有释放出牌权或缺少日志。");
                Inspect("handover-request");
                until = DateTime.UtcNow.AddSeconds(5);
                while (handoverLoads == 0 && DateTime.UtcNow < until) await Task.Delay(50);
                if (handoverLoads != 1) throw new Exception("接管没有继续读取指定原桌。");
                Inspect("handover-loaded");
                exit = 0;
                Console.WriteLine("通过 · 后台故障与接管保持隐藏、NOACTIVATE 和原前台窗口，日志及原桌导航可核对。");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.Message); }
            finally
            {
                File.WriteAllText(Path.Combine(root, "window-check.json"), JsonSerializer.Serialize(rows));
                Console.WriteLine("后台窗口证据 " + Path.Combine(root, "window-check.json"));
                desk.Close();
            }
        };
        Application.Run(desk);
        return exit;
    }
}
