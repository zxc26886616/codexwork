using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SeaSaltDesk;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--build-identity"]) { BuildIdentity.Write(); return 0; }
        if (args.FirstOrDefault() == "--stop-queue")
        {
            try
            {
                if (args is not ["--stop-queue", _] and not ["--stop-queue", _, "--root", _])
                    throw new ArgumentException();
                var root = args.Length == 4 ? args[3] :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaSaltDesk");
                new SeriesBook(root).StopQueue(args[1]);
                Console.WriteLine("已持久停止本批次新入队；已提交的匹配与原桌继续核实。");
                return 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
            {
                Console.Error.WriteLine("停排未确认，请核对即时制批次、资料目录和入队锁。");
                return 1;
            }
        }
        if (args.Contains("--series") || args.Contains("--realtime") || args.Contains("--resume")) return SeriesRunner.MainSeries(args);
        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        try
        {
            var (root, firstPage) = Options(args);
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            using var desk = new Desk(root, firstPage, background: true);
            desk.Shown += async (_, _) =>
            {
                try { await Serve(desk, input, output); }
                catch (IOException) { /* 管道断开后退出本进程。 */ }
                finally { desk.Close(); }
            };
            Application.Run(desk);
            return 0;
        }
        catch (ArgumentException)
        {
            output.WriteLine(Error("启动参数无效。可使用 --url 指定 BGA 链接，--root 指定资料目录。"));
        }
        catch (Exception)
        {
            output.WriteLine(Error("后台窗口启动失败，请检查运行时和资料目录。"));
        }
        return 1;
    }

    private static (string, Uri) Options(string[] args)
    {
        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaSaltDesk");
        string? address = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--root" or "--目录" when i + 1 < args.Length: root = Path.GetFullPath(args[++i]); break;
                case "--url" or "--地址" when i + 1 < args.Length && address == null: address = args[++i]; break;
                default:
                    if (address != null || args[i].StartsWith('-')) throw new ArgumentException();
                    address = args[i];
                    break;
            }
        }
        return (root, TableBrowser.Address(address ?? TableBrowser.Panel));
    }

    private static IEnumerable<Control> Children(Control parent) =>
        parent.Controls.Cast<Control>().SelectMany(c => Children(c).Prepend(c));

    private static string Reply(object payload) => JsonSerializer.Serialize(payload, Json);
    private static string Error(string message) => Reply(new { ok = false, message });

    private static async Task Serve(Desk desk, StreamReader input, StreamWriter output)
    {
        // 只在自己的牌桌窗体中取业务控件，不遍历其他窗口。
        var controls = Children(desk).ToArray();
        var web = controls.OfType<TableBrowser>().Single();
        var status = controls.OfType<Label>().Single(c => c.Name == "status");
        var toggle = controls.OfType<Button>().Single(c => c.Text == "启动自动出牌");
        var address = controls.OfType<TextBox>().Single(c => c.PlaceholderText == "粘贴 BGA 链接或桌号");
        var open = controls.OfType<Button>().Single(c => c.Text == "打开");
        var log = controls.OfType<RichTextBox>().Single(c => c.AccessibleName == "操作日志内容");
        bool Running() => toggle.Text == "暂停自动出牌";
        string State() => Reply(new { ok = true, url = PublicUrl(web.Source), status = status.Text, enabled = toggle.Enabled, running = Running() });

        // 阻塞的标准输入不能占用 STA 消息泵。每次只接收一条，响应写回后才读下一条。
        while (await Task.Run(() => input.ReadLine()) is { } line)
        {
            string response;
            bool quit = false;
            try
            {
                using var request = JsonDocument.Parse(line);
                var cmd = request.RootElement;
                if (cmd.ValueKind != JsonValueKind.Object || !cmd.TryGetProperty("op", out var opNode) ||
                    opNode.ValueKind != JsonValueKind.String) throw new JsonException();
                string? op = opNode.GetString();
                string? arg = cmd.TryGetProperty("arg", out var argNode) ? argNode.GetString() : null;
                switch (op)
                {
                    case "state": response = State(); break;
                    case "log": response = Reply(new { ok = true, log = log.Text }); break;
                    case "quit": quit = true; response = Reply(new { ok = true, message = "后台进程已收到退出命令。" }); break;
                    case "navigate":
                        if (web.CoreWebView2 == null) { response = Error("浏览器尚未就绪。"); break; }
                        var target = TableBrowser.Address(arg ?? "");
                        address.Text = target.AbsoluteUri;
                        open.PerformClick();
                        response = Reply(new { ok = true, message = "已请求打开页面。", url = PublicUrl(target) });
                        break;
                    case "autoplay":
                        if (arg is not ("start" or "stop" or "启动" or "暂停"))
                        { response = Error("请指定 start（启动）或 stop（暂停）。"); break; }
                        bool start = arg is "start" or "启动";
                        if (start != Running())
                        {
                            if (!toggle.Enabled) { response = Error("自动出牌按钮尚不可用。"); break; }
                            toggle.PerformClick();
                        }
                        response = Running() == start ? State() : Error("自动出牌状态尚未切换。");
                        break;
                    case "list" or "click" or "inspect" or "select" or "fill":
                        if (web.CoreWebView2 == null) { response = Error("浏览器尚未就绪。"); break; }
                        using (var result = JsonDocument.Parse(await LobbyControls.Run(web, op, arg))) response = Reply(result.RootElement);
                        break;
                    default: response = Error("未知命令。"); break;
                }
            }
            catch (JsonException) { response = Error("无效的 JSON 命令或返回值。"); }
            catch (ArgumentException) { response = Error("命令参数或 BGA 链接无效。"); }
            catch (Exception) { response = Error("命令处理失败，未能确认操作结果。"); }
            await output.WriteLineAsync(response);
            if (quit) return;
        }
    }

    private static string PublicUrl(Uri? uri)
    {
        if (!TableBrowser.IsBga(uri)) return "";
        var query = System.Web.HttpUtility.ParseQueryString(uri!.Query);
        var clean = System.Web.HttpUtility.ParseQueryString("");
        foreach (string key in new[] { "game", "table", "tutorial" })
            if (query[key] is { Length: > 0 and <= 64 } value && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) clean[key] = value;
        return new UriBuilder(uri) { UserName = "", Password = "", Query = clean.ToString(), Fragment = "" }.Uri.AbsoluteUri;
    }
}
