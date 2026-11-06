using System.Diagnostics;
using System.Text.Json;
using SeaSaltDesk;

internal static class TurnBasedSwitch
{
    private static readonly string Script = ReadScript();

    private static string ReadScript()
    {
        using var resource = typeof(TurnBasedSwitch).Assembly.GetManifestResourceStream("SeatControl.turnbased.js")!;
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    internal static async Task<bool> TryConvert(TableBrowser web, string table, string host, string guest)
    {
        var core = web.CoreWebView2;
        if (core == null || !TableBrowser.IsBga(web.Source) || !SeriesBook.ValidGuestName(guest)) return false;
        string expected = JsonSerializer.Serialize(new { table, host, guest, origin = web.Source!.GetLeftPart(UriPartial.Authority) });
        var issued = new HashSet<string>();
        var clock = Stopwatch.StartNew();
        var limit = TimeSpan.FromSeconds(90);
        while (clock.Elapsed < limit && !web.IsDisposed)
        {
            try
            {
                var command = await Step(null);
                if (command == "done") return true;
                if (command == "blocked") return false;
                // 先记录再发送。即使按钮点击引发页面重载，下一次轮询也不会重新提议或投票。
                if (command != null && command != "wait" && issued.Add(command)) await Step(command);
            }
            catch (TimeoutException) { return false; }
            catch (InvalidOperationException) { return false; }
            catch (System.Runtime.InteropServices.COMException) { return false; }
            await Task.Delay(550);
        }
        return false;

        async Task<string?> Step(string? permit)
        {
            var remaining = limit - clock.Elapsed;
            if (remaining <= TimeSpan.Zero) throw new TimeoutException();
            string js = $"({Script})({expected},{JsonSerializer.Serialize(permit)},{JsonSerializer.Serialize(issued)})";
            string json = await core.ExecuteScriptAsync(js).WaitAsync(remaining);
            return JsonSerializer.Deserialize<string>(json);
        }
    }
}
