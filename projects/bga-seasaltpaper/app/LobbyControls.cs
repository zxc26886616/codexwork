using System.Text.Json;

namespace SeaSaltDesk;

public static class LobbyControls
{
    private static readonly string Script = Load();
    private static string Load()
    {
        using var stream = typeof(TableBrowser).Assembly.GetManifestResourceStream("SeaSaltDesk.lobby.js")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static async Task<string> Run(TableBrowser web, string op, string? arg)
    {
        if (web.CoreWebView2 == null || !TableBrowser.IsBga(web.Source))
            return """{"ok":false,"message":"请先打开 BGA 的大厅或牌桌。"}""";
        var json = await web.CoreWebView2.ExecuteScriptAsync(
            $"({Script})({JsonSerializer.Serialize(op)}, {JsonSerializer.Serialize(arg ?? "")})");
        return json == "null" ? """{"ok":false,"message":"页面正在切换，请重新查看状态。"}""" : json;
    }
}
