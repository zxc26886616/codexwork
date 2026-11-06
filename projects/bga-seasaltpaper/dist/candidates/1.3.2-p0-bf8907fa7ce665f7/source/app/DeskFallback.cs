using System.Text.Json;

namespace SeaSaltDesk;

public sealed partial class Desk
{
    public sealed record FallbackNotice(string Code, string Reason, string Table, int Browser, int Generation,
        int Actions, string? OfficialStatus = null, string? Cancelled = null, string? EndReason = null, Seat[]? Scores = null);
    private sealed class Fallback(SeriesState state, Uri lobby, Func<SeriesState, SeriesState> attach,
        Action<FallbackNotice> notify)
    {
        public SeriesState State = state;
        public readonly string Table = (state.CurrentTable ?? state.OfferTable)!;
        public readonly Uri Lobby = lobby;
        public readonly Func<SeriesState, SeriesState> Attach = attach;
        public readonly Action<FallbackNotice> Notify = notify;
        public bool Bound;
        public long LastNavigation, LastEntry;
        public long? MissingAccountSince, ResultSince;
        public Uri? PrearmTarget;
        public long PrearmStarted;
        public bool TriedReplacement;
    }
    private Fallback? fallback;
    private bool fallbackUsed;
    private Uri? ControllerRecoveryAddress => armedAddress ?? fallback?.PrearmTarget;

    public void RevokeUnarmedFallbackRead()
    {
        if (fallback is not { Bound:false } guard || armed != null || guard.State.CurrentTable != null) return;
        // 旧代只有只读脚本。丢弃阻塞中的 Desk/Supervisor 读，不能让旧 finally 或晚回执给新代授权。
        ++navigation; ++tickOwner; busy = false;
        revoked.TrySetResult(); revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var uri = browser.Source;
        if (uri == null || uri.GetLeftPart(UriPartial.Authority) != guard.Lobby.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath is not ("/lobby" or "/gamepanel" or "/table" or "/tableview") &&
                !uri.AbsolutePath.EndsWith("/seasaltpaper", StringComparison.Ordinal) ||
            System.Web.HttpUtility.ParseQueryString(uri.Query)["table"] is { } number && number != guard.Table) return;
        guard.PrearmTarget = new Uri(guard.Lobby, "/table?table=" + guard.Table + "&nr=true");
        guard.PrearmStarted = guard.LastNavigation = Now;
        try { browser.CoreWebView2.Navigate(guard.PrearmTarget.AbsoluteUri); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException) { }
    }

    // 仅承接本进程已经确认的原桌。调用方先永久停续排；这里没有匹配、接受或重启 Play 的能力。
    public bool BeginMatchedFallback(SeriesState state, Uri lobby, Func<SeriesState, SeriesState> attach,
        Action<FallbackNotice> notify)
    {
        if (fallbackUsed || !state.Realtime || state.Finished || state.OpponentId == null ||
            (state.CurrentTable ?? state.OfferTable) == null ||
            armed != null && !IsRunningMatchedSeat(state)) return false;
        fallbackUsed = true;
        batchVerifiesResults = true;
        fallback = new(state, lobby, attach, notify) { Bound = IsRunningMatchedSeat(state) };
        return true;
    }

    private void NotifyFallback(Fallback guard, string code, string reason, RealtimeSeries.Page? page = null, Seat[]? scores = null)
    {
        guard.Notify(new(code, reason, guard.Table, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(browser),
            navigation, MatchedActions, page?.OfficialStatus, page?.Cancelled, page?.EndReason, scores));
    }

    private void FinishFallback(string code, string reason, RealtimeSeries.Page? page = null, Seat[]? scores = null)
    {
        var guard = fallback;
        if (guard == null) return;
        fallback = null;
        // 终局及拒绝只撤销本进程授权，不删原意图、不伪造战绩，也不释放批次原桌关联。
        Disarm();
        NotifyFallback(guard, code, reason, page, scores);
    }

    private async Task<bool> TickFallbackAsync(int revision, int owner)
    {
        var guard = fallback!;
        var current = browser;
        var uri = current.Source;
        if (uri == null || current.CoreWebView2 == null) return false;
        if (guard.PrearmTarget != null && guard.TriedReplacement && uri.AbsoluteUri == "about:blank") return false;
        string? table = System.Web.HttpUtility.ParseQueryString(uri.Query)["table"];
        if (uri.GetLeftPart(UriPartial.Authority) != guard.Lobby.GetLeftPart(UriPartial.Authority) ||
            uri.AbsolutePath == "/account" || table != null && table != guard.Table)
        {
            FinishFallback("supervisor-fallback-rejected", "site-table-or-account");
            return false;
        }
        // 已消费的接席授权不再包裹独立出牌器：断网、客户端锁和处罚后的原桌恢复沿用原 Tick。
        if (IsRunningMatchedSeat(guard.State)) return true;
        if (uri.AbsolutePath is "/lobby" or "/gamepanel")
        {
            if (guard.PrearmTarget is { } target)
            {
                // 请求可能已有HTTP 200，旧renderer却仍占着原文档。先测JS是否能返回，不连续Navigate。
                var lost = ControllerRevoked;
                var ping = current.CoreWebView2.ExecuteScriptAsync("location.pathname");
                var first = await Task.WhenAny(ping, lost, Task.Delay(5000));
                if (first != ping || lost.IsCompleted)
                    _ = ping.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                else if (Current()) {
                    _ = await ping;
                    if (Now - guard.LastNavigation >= 5000) { guard.LastNavigation = Now; current.CoreWebView2.Navigate(target.AbsoluteUri); }
                }
                return false;
            }
            if (guard.Bound) FinishFallback("supervisor-fallback-rejected", "left-bound-table");
            else NavigateOriginal();
            return false;
        }
        if (uri.AbsolutePath != "/table" && uri.AbsolutePath != "/tableview" &&
            !uri.AbsolutePath.EndsWith("/seasaltpaper", StringComparison.Ordinal))
        {
            FinishFallback("supervisor-fallback-rejected", "unsupported-page");
            return false;
        }
        var revokedRead = ControllerRevoked;
        var reading = LobbyControls.Run(current, "realtime", JsonSerializer.Serialize(new {
            table = guard.Table, host = guard.State.HostId, opponent = guard.State.OpponentId,
            attached = guard.State.CurrentTable != null, emergency = true }));
        // 原观察随导航失效；读超时只重读原桌，不允许晚到回执在另一文档上授权接席。
        var done = await Task.WhenAny(reading, revokedRead, Task.Delay(5000));
        if (done != reading || revokedRead.IsCompleted)
        {
            _ = reading.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (done != revokedRead && Current()) NavigateOriginal();
            return false;
        }
        var page = JsonSerializer.Deserialize<RealtimeSeries.Page>(await reading, MatchBook.Json);
        if (!Current() || page == null) return false;
        // 文档 HTTP 200 不代表 renderer 已复活；仅在原桌 JS 真实返回后释放未接席恢复目标。
        if (guard.PrearmTarget != null && uri.AbsolutePath == "/table" && table == guard.Table && page.Host == guard.State.HostId)
            guard.PrearmTarget = null;
        if (uri.AbsolutePath == "/table" && table == guard.Table && page.Kind == "wait" && page.Host == "")
        {
            // 新结算文档先于登录信息载入。此处只读同桌；已有的非空错号仍立即撤权。
            guard.MissingAccountSince ??= Now;
            if (Now - guard.MissingAccountSince >= 8000)
                FinishFallback("supervisor-fallback-rejected", "account-load-timeout");
            return false;
        }
        guard.MissingAccountSince = null;
        if (page.Kind == "blocked" || page.Host != null && page.Host != guard.State.HostId ||
            page.Table != null && page.Table != guard.Table ||
            page.Opponent != null && page.Opponent != guard.State.OpponentId)
        {
            var reason = uri.AbsolutePath == "/table" && !string.IsNullOrEmpty(page.Host) && page.Host != guard.State.HostId
                ? "wrong-account" : page.Code ?? "identity-or-rules";
            FinishFallback("supervisor-fallback-rejected", reason);
            return false;
        }
        if (page.Kind is "result" or "void" or "non-normal")
        {
            if (page.Table != guard.Table || page.Code != "allocated-authority") return false;
            Seat[]? scores = null;
            if (page.Kind == "result")
            {
                // 救场只归档同桌官方比分。读取用保留原意图的只读入口，不写正常战绩或配额。
                guard.ResultSince ??= Now;
                var scoreRead = current.ReadAsync();
                var finished = await Task.WhenAny(scoreRead, revokedRead, Task.Delay(3000));
                if (finished != scoreRead)
                    _ = scoreRead.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                if (!Current() || revokedRead.IsCompleted) return false;
                if (finished == scoreRead && (await scoreRead).Result is { } match)
                {
                    if (!MatchBook.Valid(match) || match.Site != guard.State.Site || match.Table != guard.Table ||
                        match.Players.Length != 2 || !match.Players.Any(s => s.Id == guard.State.HostId) ||
                        !match.Players.Any(s => s.Id == guard.State.OpponentId))
                    {
                        FinishFallback("supervisor-fallback-rejected", "result-seat-mismatch", page);
                        return false;
                    }
                    scores = match.Players;
                }
                if (scores == null && Now - guard.ResultSince < 3000) return false;
            }
            FinishFallback("supervisor-fallback-ended", page.Kind, page, scores);
            return false;
        }
        if (page.Kind == "started" || page.Kind == "enter")
        {
            if (page.Code == "allocated-authority" && Now - guard.LastEntry >= 4000)
            {
                guard.LastNavigation = guard.LastEntry = Now;
                Navigate(new Uri(guard.Lobby, "/tableview?table=" + guard.Table));
            }
            return false;
        }
        if (page.Kind != "game")
        {
            if (page.Code == "allocated-read-timeout" || page.Code == "allocated-reader-missing" &&
                Now - guard.LastNavigation >= 5000) NavigateOriginal();
            return false;
        }
        if (page.Table != guard.Table || page.Host != guard.State.HostId || page.Opponent != guard.State.OpponentId)
        {
            FinishFallback("supervisor-fallback-rejected", "game-seat-mismatch");
            return false;
        }
        if (page.Phase == "gameEnd") { NavigateOriginal(terminal: true); return false; }
        if (IsRunningMatchedSeat(guard.State)) return true;
        if (guard.Bound || armed != null)
        {
            FinishFallback("supervisor-fallback-rejected", "bound-controller-revoked");
            return false;
        }
        // ready 本身没有完整规则核对能力；只有同一代上面的 game 核对通过后才允许接席。
        var readyTask = current.ReadAsync();
        done = await Task.WhenAny(readyTask, revokedRead, Task.Delay(5000));
        if (done != readyTask || revokedRead.IsCompleted)
        {
            _ = readyTask.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (done != revokedRead && Current()) NavigateOriginal();
            return false;
        }
        var readyRead = await readyTask;
        if (!Current()) return false;
        string key = $"{guard.State.Site}/{guard.Table}/座位/{guard.State.HostId}";
        if (readyRead.Kind != "ready" || readyRead.Table != key) return false;
        guard.State = guard.Attach(guard.State);
        ready = key;
        toggle.Enabled = true;
        if (StartMatchedSeat(guard.State.Site, guard.Table, guard.State.HostId, guard.State.OpponentId!))
        {
            guard.Bound = true;
            NotifyFallback(guard, "supervisor-fallback-armed", "same-controller-original-seat");
            return true;
        }
        return false;

        bool Current() => !IsDisposed && fallback == guard && current == browser &&
            revision == navigation && owner == tickOwner;
        void NavigateOriginal(bool terminal = false)
        {
            guard.LastNavigation = Now;
            // 已有席位优先沿用保留 pending 的同桌恢复；未接席仅打开已确认的原桌只读信息。
            if (!terminal && IsRunningMatchedSeat(guard.State)) RecoverTable("救场仅核对原桌，保留待确认动作。");
            else Navigate(new Uri(guard.Lobby, "/table?table=" + guard.Table + "&nr=true"));
        }
    }
}
