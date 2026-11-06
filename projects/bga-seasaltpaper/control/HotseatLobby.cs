using System.Text.Json;
using SeaSaltDesk;

internal sealed record LobbyStep(string State, string Message, string? Table = null, string? HostId = null);

internal sealed class HotseatLobby(Action? beforeCreate = null, bool turnBased = false, string? convertingTable = null)
{
    private readonly HashSet<string> issued = [];
    private readonly HashSet<string> reconciled = [];
    private string? attached, hostId, stopReason;
    private DateTime lastAction;
    private static readonly HashSet<(string Origin, string Host, string Member, string Guest)> confirmedLocalGuests = [];

    public async Task<LobbyStep> Tick(TableBrowser web, string guest)
    {
        if (stopReason != null) return new("blocked", stopReason, attached, hostId);
        if (!SeriesBook.ValidGuestName(guest))
            return Stop("客席昵称须为 1 到 16 个字，可使用文字、数字、空格、横线或下划线。");
        using var response = JsonDocument.Parse(await LobbyControls.Run(web, "series-info", turnBased ? "turn-based" : ""));
        var info = response.RootElement;
        if (!Flag(info, "ok")) return Wait(Text(info, "message"));
        string page = Text(info, "page"), id = Text(info, "table"), host = Text(info, "host");
        if (hostId != null && host != hostId) return Stop("当前账号已经改变。");
        hostId = host;
        if (page == "lobby")
        {
            if (attached != null) return Stop("已离开开好的游戏桌，请检查桌号。");
            if (Flag(info, "splash"))
                return Text(info, "dismiss") is { Length: > 0 } dismiss
                    ? await Submit(web, dismiss, "关闭官方成就提示") : Wait("等待成就提示的继续按钮。");
            if (Text(info, "wizard") is { Length: > 0 } wizard)
                return await Submit(web, wizard, "选择同屏游戏偏好");
            if (turnBased && Text(info, "clockMode") != "async") return Wait("等待大厅确认选择了官方回合制。");
            int count = info.GetProperty("partners").GetInt32();
            if (count > 0)
            {
                if (count != 1 || Text(info, "partner") != guest) return Stop("大厅已有其他伙伴，未创建游戏桌。");
                var member = (web.Source.GetLeftPart(UriPartial.Authority), host, Text(info, "memberId"), guest);
                if (Flag(info, "confirmed") && (issued.Contains("validate_hotseat_player_name") || Flag(info, "localMember")))
                    confirmedLocalGuests.Add(member);
                if (!Flag(info, "confirmed")) return Wait("等待大厅确认唯一的本地伙伴。");
                if (!confirmedLocalGuests.Contains(member))
                {
                    if (!long.TryParse(member.Item3, out var selectedId) || selectedId <= 0)
                        return Stop("无法识别大厅中现有伙伴的席位。");
                    // 官方大厅给普通伙伴和先前的热座同样的 member 标记。只移除唯一同名旧伙伴，再走同屏入口。
                    return await Submit(web, "gamesession_player_" + member.Item3, "重置同名旧伙伴以重新确认同屏");
                }
                if (Flag(info, "tutorial")) return await Tutorial(web, info);
                if (Flag(info, "play")) return await Submit(web, "Xutton_play_1631", "创建海盐折纸游戏桌");
                return Wait("等待海盐折纸的开桌按钮。");
            }
            if (issued.Contains("Xutton_play_1631")) return AwaitAction(web, "Xutton_play_1631", "等待新游戏桌。");
            if (issued.Contains("validate_hotseat_player_name")) return AwaitAction(web, "validate_hotseat_player_name", "等待大厅确认本地热座。");
            if (Flag(info, "tutorial")) return await Tutorial(web, info);
            if (info.GetProperty("nickname").ValueKind == JsonValueKind.String)
                return Text(info, "nickname") == guest
                    ? await Submit(web, "validate_hotseat_player_name", "添加本地客席")
                    : await Submit(web, "hotseat_player_name", "填写本地客席昵称", "fill", guest);
            if (Flag(info, "nearby")) return await Submit(web, "friend_nearby_button", "选择同屏游玩");
            if (Flag(info, "expand")) return await Submit(web, "playwithfriend_link", "展开与朋友游戏");
            if (Flag(info, "addFriend")) return await Submit(web, "lobby_add_friend_button", "添加同屏伙伴");
            return Wait("等待大厅的同屏游玩设置。");
        }
        if (attached != null && id != attached) return Stop("页面桌号与已确认桌号不同。");
        if (page == "game")
        {
            using var game = JsonDocument.Parse(await LobbyControls.Run(web, "inspect", "牌局"));
            var g = game.RootElement;
            if (!Flag(g, "内页就绪")) return Wait("等待游戏框加载。");
            if (turnBased && Boolean(g, "回合制") != true)
                return Boolean(g, "回合制") == false ? Stop("实际游戏框为即时制，已停止。") : Wait("等待游戏框确认官方回合制。");
            if (Text(g, "当前游戏") != "seasaltpaper" || Text(g, "热座模式") != "normal" ||
                Text(g, "房主座位") != host || !g.TryGetProperty("公开名次板", out var roster) ||
                roster.GetArrayLength() != 2 || !g.TryGetProperty("本机座位", out var local) || local.ValueKind != JsonValueKind.Array ||
                local.GetArrayLength() != 2 || !roster.EnumerateArray().Select(x => x.ToString()).ToHashSet()
                    .SetEquals(local.EnumerateArray().Select(x => x.ToString())))
                return Stop("当前游戏不符合房主和唯一客席均为本机热座的条件。");
            return new("playing", "双人本地热座牌桌已就绪。", id, host);
        }
        if (page != "table") return Wait("等待游戏桌。");
        if (Flag(info, "ended")) return new("wait", "官方结算已显示。", attached, host);
        bool? actualMode = Boolean(info, "turnBased");
        if (turnBased && actualMode == null) return Wait("等待官方游戏速度和桌面状态同步。");
        int seats = info.GetProperty("seats").GetInt32();
        if (seats < 2) return Wait("等待两席加入游戏桌。");
        if (seats != 2 || info.GetProperty("localSeats").GetInt32() != 1 || !Flag(info, "mySeat") || Text(info, "guest") != guest)
            return Stop("入座者与指定的双人热座不符。");
        bool enter = Flag(info, "enter");
        if (actualMode != null && actualMode != turnBased)
        {
            // 已绑定的原桌只能进入已有游戏，转换速度由游戏内的官方流程确认。
            if (turnBased && id == convertingTable && Text(info, "tableState") == "play" && Flag(info, "isHost") && enter)
                return await Submit(web, "access_game_normal", "进入原桌以转换游戏速度");
            return Stop("牌桌的官方游戏速度与本批次要求不符，未启动。");
        }
        if (!enter && !Flag(info, "privateTable"))
            return Wait("等待确认游戏桌尚未对外公开。");
        if (attached == null)
        {
            attached = id;
            return new("created", "已核实新桌号及双人热座。", id, host);
        }
        if (enter && issued.Contains("startgame")) return await Submit(web, "access_game_normal", "进入游戏");
        foreach (var option in info.GetProperty("options").EnumerateArray())
        {
            string value = Text(option, "value");
            if (value == "1") continue;
            if (value.Length == 0) return Wait("等待友谊赛与基础版选项载入。");
            if (enter || !Flag(option, "editable")) return Stop("游戏桌的模式或规则不符合要求，且当前无法调整。");
            return await Submit(web, Text(option, "id"), "设置双人友谊赛规则", "select", "1");
        }
        if (enter) return await Submit(web, "access_game_normal", "进入游戏");
        if (Flag(info, "start")) return await Submit(web, "startgame", "开始双人热座");
        return Wait("等待游戏桌可开始。");
    }

    public static async Task<bool?> ReadTurnBased(TableBrowser web)
    {
        using var result = JsonDocument.Parse(await LobbyControls.Run(web, "inspect", "牌局"));
        return Boolean(result.RootElement, "回合制");
    }

    private static bool? Boolean(JsonElement item, string key) => item.TryGetProperty(key, out var val)
        && val.ValueKind is JsonValueKind.True or JsonValueKind.False ? val.GetBoolean() : null;
    private static bool Flag(JsonElement item, string key) =>
        item.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.True;
    private static string Text(JsonElement item, string key) =>
        item.TryGetProperty(key, out var val) && val.ValueKind is JsonValueKind.String or JsonValueKind.Number ? val.ToString() : "";
    private LobbyStep Wait(string message) => new("wait", message, attached, hostId);
    private LobbyStep Stop(string message) => new("blocked", stopReason = message, attached, hostId);
    private LobbyStep AwaitAction(TableBrowser web, string action, string message)
    {
        var elapsed = DateTime.UtcNow - lastAction;
        if (elapsed > TimeSpan.FromSeconds(8) && (action == "validate_hotseat_player_name" || action.StartsWith("gamesession_player_", StringComparison.Ordinal))
            && web.Source.AbsolutePath == "/lobby" && reconciled.Add(action))
        {
            // 大厅偶尔停留在旧的伙伴列表。只刷新当前大厅核对服务器状态，不再次添加或移除。
            web.CoreWebView2.Reload();
            return Wait("正在刷新大厅，核对同屏伙伴。");
        }
        return elapsed > TimeSpan.FromSeconds(50) ? Stop("页面未确认上一步，已停止重复操作。" + message) : Wait(message);
    }

    private Task<LobbyStep> Tutorial(TableBrowser web, JsonElement info) =>
        Submit(web, "newArchiveCommentNext", "继续大厅指引", stepKey: "tutorial/" + Text(info, "tutorialText"));

    private async Task<LobbyStep> Submit(TableBrowser web, string id, string title, string op = "click", string? value = null, string? stepKey = null)
    {
        string key = stepKey ?? id;
        if (issued.Contains(key)) return AwaitAction(web, key, "等待页面确认：" + title + "。");
        using var list = JsonDocument.Parse(await LobbyControls.Run(web, "list", id));
        if (!list.RootElement.TryGetProperty("控件", out var controls)) return Wait("等待页面控件载入。");
        var matches = controls.EnumerateArray().Where(e => Text(e, "标识") == id && !Flag(e, "禁用")).ToArray();
        if (matches.Length != 1) return Wait("等待可用控件：" + title + "。");
        string token = Text(matches[0], "编号");
        if (id == "Xutton_play_1631") beforeCreate?.Invoke();
        issued.Add(key);
        lastAction = DateTime.UtcNow;
        using var result = JsonDocument.Parse(await LobbyControls.Run(web, op, value == null ? token : token + "|" + value));
        if (!Flag(result.RootElement, "ok"))
        {
            string message = Text(result.RootElement, "message");
            // 页面跳转可能先于点击回执。保留已提交标记，下一轮以桌号确认。
            if (message.Contains("页面正在切换")) return Wait("等待页面跳转。");
            if (id != "Xutton_play_1631" && message.StartsWith("控件被其他面板遮挡", StringComparison.Ordinal))
            {
                issued.Remove(key);
                return Wait("等待遮挡面板关闭：" + title + "。");
            }
            return Stop("未确认操作「" + title + "」。" + message);
        }
        return Wait("已提交：" + title + "。");
    }
}
