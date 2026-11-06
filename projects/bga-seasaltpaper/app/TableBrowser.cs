using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace SeaSaltDesk;

public sealed record SeatPlan(string DocumentId, string Phase, long? Move, string Board, string Target, string Local,
    string? ActionKind = null, bool OfficialOwn = false, string? RecoveryCard = null);
public sealed record OpponentTurn(string DocumentId, string Phase, long Move);
public sealed record Reading(string Kind, string Message, string? Table = null, Match? Result = null, string? Phase = null,
    SeatPlan? Plan = null, bool NotSubmitted = false, int? ClockSeconds = null, string? WaitReason = null,
    string? ClockStatus = null, string? ClockSource = null, string? ClockPhase = null,
    string? PhaseSource = null, DateTimeOffset? ClockReadAt = null, OpponentTurn? OpponentTurn = null);

public sealed class TableBrowser : WebView2
{
    public const string Panel = "https://boardgamearena.com/gamepanel?game=seasaltpaper";
    public const string Tutorial = "https://zh-cn.boardgamearena.com/tutorial?game=seasaltpaper&tutorial=1027";
    internal const string PlayerOrigin = "https://en.boardgamearena.com", StudioOrigin = "https://studio.boardgamearena.com";
    private readonly string adapter;
    private readonly string loginAdapter;
    private ulong? loadingNavigation;

    public TableBrowser()
    {
        adapter = Script("table");
        loginAdapter = Script("login");
    }

    private static string Script(string name)
    {
        using var resource = typeof(TableBrowser).Assembly.GetManifestResourceStream($"SeaSaltDesk.{name}.js")!;
        using var reader = new StreamReader(resource);
        return reader.ReadToEnd();
    }

    public async Task<string> LoginStepAsync(string origin, string operation, string username, string value = "")
    {
        if (origin != PlayerOrigin && origin != StudioOrigin) return "wrong-origin";
        if (loadingNavigation != null || CoreWebView2 == null || Source == null || Source.AbsoluteUri == "about:blank")
            return "loading";
        if (Source.GetLeftPart(UriPartial.Authority) != origin) return "wrong-origin";
        var args = new[] { origin, operation, username, value }.Select(s => JsonSerializer.Serialize(s));
        var reply = await CoreWebView2.ExecuteScriptAsync($"({loginAdapter})({string.Join(",", args)})");
        // 跳转会使脚本回执丢失，此时不能断定点击尚未发生。
        return JsonSerializer.Deserialize<string>(reply) ?? "unconfirmed";
    }

    public static bool IsBga(Uri? url) => url is { Scheme: "https", IsDefaultPort: true }
        && (url.IdnHost == "boardgamearena.com" || url.IdnHost.EndsWith(".boardgamearena.com", StringComparison.Ordinal));

    public static Uri Address(string text)
    {
        text = text.Trim();
        if (text.Length is >= 1 and <= 18 && text.All(char.IsAsciiDigit) && text[0] != '0')
            return new Uri("https://boardgamearena.com/table?table=" + text);
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && IsBga(uri) && uri.UserInfo.Length == 0)
            return uri;
        throw new ArgumentException("请输入 BGA 的 HTTPS 链接或纯数字桌号。");
    }

    public async Task InitializeAsync(string profile)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: profile);
        await EnsureCoreWebView2Async(environment);
        CoreWebView2.Settings.AreHostObjectsAllowed = false;
        CoreWebView2.Settings.IsWebMessageEnabled = false;
        CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        CoreWebView2.NavigationStarting += (_, e) => loadingNavigation = e.NavigationId;
        CoreWebView2.DOMContentLoaded += (_, e) => { if (e.NavigationId == loadingNavigation) loadingNavigation = null; };
        CoreWebView2.NavigationCompleted += (_, e) => { if (e.NavigationId == loadingNavigation) loadingNavigation = null; };
        CoreWebView2.NewWindowRequested += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !IsBga(uri)) return;
            e.Handled = true;
            Source = uri;
        };
    }

    public Task<Reading> ReadAsync(string? armedTable = null, string? unconfirmedPhase = null, string? opponent = null)
        => ReadTable(armedTable, unconfirmedPhase, opponent, null);

    internal Task<Reading> ReadMatched(string table, string opponent, SeatPlan? commit = null, bool recoverDraw = false)
        => ReadTable(table, null, opponent, new { commit, recoverDraw, expires = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000 });

    private async Task<Reading> ReadTable(string? armedTable, string? unconfirmedPhase, string? opponent, object? dispatch)
    {
        if (armedTable != null && opponent != null && loadingNavigation.HasValue)
            return new("wait", "原桌正在重新载入，保留已绑定的出牌座位。", armedTable);
        if (CoreWebView2 == null || !IsBga(Source)) return new("idle", "请打开 BGA 牌桌或教程。");
        var json = await CoreWebView2.ExecuteScriptAsync(
            $"({adapter})({JsonSerializer.Serialize(armedTable)}, {JsonSerializer.Serialize(unconfirmedPhase)}, {JsonSerializer.Serialize(opponent)}, {JsonSerializer.Serialize(dispatch, MatchBook.Json)})");
        return JsonSerializer.Deserialize<Reading>(json, MatchBook.Json) ?? new("wait", "页面正在载入。");
    }
}
