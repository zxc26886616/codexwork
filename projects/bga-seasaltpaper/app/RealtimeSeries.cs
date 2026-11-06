using System.Text.Json;

namespace SeaSaltDesk;

public sealed class VoidedRealtimeMatchException(string message) : Exception(message);
public sealed class PrearmObservationTimeoutException() : TimeoutException("已确认匹配后，未接席的原桌只读观察没有返回。");

public sealed class RealtimeSeries
{
    internal sealed record Page(string? Kind, string? Message, string? Host, string? Table, string? Opponent, string? Action,
        string? Preference, bool? MyTurn, string? Phase, string? EndReason, string? Cancelled, string? OfficialStatus, string? Code,
        long? AuthorityAt = null)
    {
        public string Key => string.Join("|", Kind, Host, Table, Opponent, Action);
    }

    // 由桌面后台和受控 WebView2 场景使用同一条批次循环。
    public static async Task Play(Desk desk, SeriesBook book, SeriesState batch, Uri lobby, Action<SeriesState, string> report,
        Func<bool>? queueHeld = null, bool guardedPrearm = false, bool recoverCurrentSeat = false)
    {

        desk.RequireVerifiedResults();
        var resumedTable = batch.CurrentTable;
        bool verifiedResumedTable = false, unavailableJournal = false;
        if (resumedTable != null) desk.OpenPage(new Uri(lobby, "/table?table=" + resumedTable + "&nr=true"));
        string? lastClick = null, armed = null, previous = null;
        string? recordedEnd = null;
        string? acceptedHere = null;
        bool sawQueue = false, platformBlocked = false, penaltySeatLossReported = false, allocationHeld = false;
        bool drawHandoffReported = false;
        var reloadedPreferences = new HashSet<string>();
        DateTime? acceptedAt = batch.OfferTable == null ? null : DateTime.UtcNow;
        DateTime? offerChecked = null;
        int tableChecks = 0;
        DateTime? enteredAt = null, ownTurnSince = null;
        long? playVerifiedAt = null, viewOpenedAt = null, officialReplyAt = null;
        int viewRetries = 0;
        bool viewHoldReported = false;
        string? lastTurn = null;
        DateTime changed = DateTime.UtcNow;
        bool QueueHeld() => platformBlocked || allocationHeld || drawHandoffReported || queueHeld?.Invoke() == true || book.QueueStopped(batch.Id);
        bool WaitingForFirstSeat() => playVerifiedAt.HasValue && batch.OfferTable != null && batch.CurrentTable == null &&
            armed == null && !desk.IsPlaying;
        void WarnIfSeatUnready()
        {
            if (viewHoldReported || !WaitingForFirstSeat()) return;
            long waiting = Environment.TickCount64 - playVerifiedAt!.Value;
            if (waiting < 8000) return;
            viewHoldReported = allocationHeld = true;
            try { book.StopQueue(batch.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
                report(batch, "无法保存停排标记，本进程已停止新匹配。");
            }
            var evidence = new { status = "play", table = batch.OfferTable, self = batch.HostId, opponent = batch.OpponentId,
                authorityAtUnixMs = officialReplyAt, clockStatus = "not-read" };
            if (!book.RecordIncident(batch, "allocated-play-seat-not-ready", 0, waiting, evidence))
                report(batch, "无法保存原桌开局后未接席的事故记录。");
            desk.ShowMatchedTable($"桌号 {batch.OfferTable} 官方已开局 {waiting / 1000} 秒，尚未读取己方棋钟；已停续排，继续核对原桌。");
        }
        bool StopWithoutOutstandingTable() => batch.CurrentTable == null && batch.OfferTable == null && !batch.QueuePending && QueueHeld();
        void StopForPenalty(string message)
        {
            if (platformBlocked) return;
            platformBlocked = true;
            try { book.StopQueue(batch.Id); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
            {
                report(batch, "无法保存停排记录，本进程已停止新匹配。");
            }
            if (!book.RecordIncident(batch, "platform-penalty", desk.MatchedActions))
                report(batch, "无法保存平台提醒记录，请检查本机日志目录。");
            report(batch, message);
        }
        while (!batch.Finished)
        {
            await Task.Delay(350);
            if (StopWithoutOutstandingTable()) return;
            if (desk.IsDisposed) throw new InvalidOperationException("批次窗口已关闭。");
            if (desk.MatchedPenaltyNotice is { } penalty) StopForPenalty(penalty);
            if (desk.ReplacingBrowser) continue;
            var web = desk.GameBrowser;
            var uri = web.Source;
            if (desk.DrawHandoffMessage is { } handoff && batch.CurrentTable != null)
            {
                if (!drawHandoffReported)
                {
                    drawHandoffReported = true;
                    report(batch, handoff);
                    try { book.StopQueue(batch.Id); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
                    {
                        desk.ShowMatchedTable(handoff + " 未能写入停排标记，本进程仍保留接管状态。");
                    }
                    if (!book.RecordIncident(batch, "draw-receipt-handoff-required", desk.MatchedActions))
                        desk.ShowMatchedTable(handoff + " 未能写入事故记录。");
                }
                // 原 WebView 继续留在席位上；只在原桌进入结算页后核对终局。
                if (uri?.AbsolutePath != "/table") continue;
            }
            if (uri == null || uri.Scheme == "about") { WarnIfSeatUnready(); continue; }
            if (!TableBrowser.IsBga(uri) || (batch.Site == "Studio") !=
                (uri.Host == "studio.boardgamearena.com" || uri.Host.EndsWith(".studio.boardgamearena.com", StringComparison.Ordinal)))
                throw new InvalidOperationException("页面离开了本批次的站点。");
            if (uri.AbsolutePath == "/account") throw new InvalidOperationException("登录会话已失效。");
            if (batch.OfferTable != null && uri.AbsolutePath == "/lobby" && offerChecked == null &&
                acceptedAt.HasValue && DateTime.UtcNow - acceptedAt >= TimeSpan.FromSeconds(4))
            {
                CheckAllocatedTable();
                continue;
            }
            var revoked = desk.ControllerRevoked;
            var observation = LobbyControls.Run(web, "realtime", JsonSerializer.Serialize(new {
                table = batch.CurrentTable ?? batch.OfferTable, host = batch.HostId, opponent = batch.OpponentId,
                attached = batch.CurrentTable != null, heldOffer = allocationHeld && batch.OfferTable != null }));
            var readStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            bool awaitingSeat = guardedPrearm && acceptedHere != null && batch.OfferTable == acceptedHere && batch.CurrentTable == null && !desk.IsPlaying;
            var first = await Task.WhenAny(observation, revoked, Task.Delay(awaitingSeat ? 2500 : 10000));
            if (awaitingSeat && !observation.IsCompleted && !revoked.IsCompleted && !desk.IsPlaying)
            {
                // 只有本轮已接受、尚未接席的纯观察有短预算；不能让永不返回的读取挡住独立接席。
                _ = observation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                throw new PrearmObservationTimeoutException();
            }
            bool slow = !observation.IsCompleted && first != revoked;
            if (slow)
            {
                // 读慢不等于退桌。保持同一读请求和座位定时器，不能因监督器超时撤销出牌。
                if (!book.RecordIncident(batch, "page-read-slow", desk.MatchedActions, 10000))
                    desk.ShowMatchedTable("无法写入异常记录，请检查本机日志目录");
                report(batch, "页面读取超过 10 秒，正在等待原请求，保留己方出牌状态。");
                if ((batch.CurrentTable ?? batch.OfferTable) != null)
                    desk.ShowMatchedTable("原桌页面读取延迟，正在等待恢复");
            }
            // 旧代读请求可能永远不返回。失权后只丢弃读结果，绝不让它触发下一次点击。
            if (await Task.WhenAny(observation, revoked) == revoked || revoked.IsCompleted)
            {
                _ = observation.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                continue;
            }
            Page? page;
            try { page = JsonSerializer.Deserialize<Page>(await observation, MatchBook.Json); }
            catch (System.Runtime.InteropServices.COMException) when (desk.IsPlaying && batch.CurrentTable != null &&
                web.Source?.Query == uri.Query && web.Source?.GetLeftPart(UriPartial.Authority) == uri.GetLeftPart(UriPartial.Authority))
            {
                report(batch, "原桌读取暂时失败，保留出牌器并重新核对。");
                continue;
            }
            if (slow)
            {
                long elapsedMs = (long)System.Diagnostics.Stopwatch.GetElapsedTime(readStarted).TotalMilliseconds;
                if (!book.RecordIncident(batch, "page-read-returned", desk.MatchedActions, elapsedMs))
                    desk.ShowMatchedTable("无法写入异常记录，请检查本机日志目录");
                report(batch, $"原页面读取已返回（{elapsedMs} 毫秒），继续核对本桌。");
            }
            if (page == null) { WarnIfSeatUnready(); continue; }
            string message = page.Message ?? "正在加载匹配页面。";
            if (page.Kind == "blocked" && page.Code != "platform-penalty" && batch.CurrentTable != null && uri.AbsolutePath == "/table")
                throw StopForBoundConflict(message);
            if (!string.IsNullOrEmpty(page.Host) && page.Host != batch.HostId)
                throw new InvalidOperationException("匹配账号发生了变化。");
            if (page.Code == "platform-penalty")
            {
                StopForPenalty(message);
                if (armed != null && !desk.IsRunningMatchedSeat(batch) && !penaltySeatLossReported)
                {
                    penaltySeatLossReported = true;
                    if (!book.RecordIncident(batch, "platform-penalty-seat-revoked", desk.MatchedActions))
                        report(batch, "无法保存控制器失权记录，请检查本机日志目录。");
                    report(batch, "原出牌控制器已失权，已停止新匹配并保留原桌关联，等待核实。");
                }
                if (!batch.QueuePending && batch.OfferTable == null && batch.CurrentTable == null)
                    throw new InvalidOperationException(message);
                // 在途请求可能已分桌。保留原关联和独立出牌器，避免新分类触发接管/撤位。
                continue;
            }
            if (page.Kind == "blocked") {
                if ((batch.CurrentTable ?? batch.OfferTable) != null) book.StopQueue(batch.Id);
                throw new InvalidOperationException(message);
            }
            bool waitingForFirstSeat = WaitingForFirstSeat();
            if (waitingForFirstSeat && page.Kind is "wait" or "started" or "allocated-wait") {
                WarnIfSeatUnready();
                // 已验证原桌且无席位。空文档只早重试一次；正在初始化的游戏及随后重试留足连续载入时间。
                bool loadingGame = page.Code is "game-shell-empty" or "game-initializing";
                int delay = viewRetries switch {
                    0 => page.Code == "game-initializing" ? 6500 : 2500,
                    1 => 8000,
                    _ => 30000
                };
                if (loadingGame && page.Table == batch.OfferTable && viewOpenedAt.HasValue &&
                    Environment.TickCount64 - viewOpenedAt.Value >= delay) {
                    viewRetries++;
                    viewOpenedAt = null;
                    enteredAt = null;
                    report(batch, $"官方已开局，但原游戏文档尚未就绪；第 {viewRetries} 次只读核对同桌。");
                    CheckAllocatedTable();
                    continue;
                }
            }
            var key = page.Key;
            if (key != previous) { changed = DateTime.UtcNow; previous = key; }
            var elapsed = DateTime.UtcNow - changed;
            if ((batch.CurrentTable ?? batch.OfferTable) != null && page.Kind is "wait" or "started" or "enter" &&
                elapsed > TimeSpan.FromSeconds(12))
                desk.ShowMatchedTable("原桌载入超过 12 秒，可在窗口查看或接管");
            if (batch.CurrentTable != null && page.Table != null && page.Table != batch.CurrentTable)
                throw new InvalidOperationException("页面桌号与批次当前桌不符。");
            if (drawHandoffReported && page.Kind is not ("result" or "void" or "non-normal")) continue;
            if (page.Kind is "result" or "void" or "non-normal" && page.Table != null &&
                page.Table == (batch.CurrentTable ?? batch.OfferTable) && page.Table != recordedEnd)
            {
                var end = new { at = DateTimeOffset.UtcNow, batch = batch.Id, site = batch.Site, table = page.Table,
                    status = page.OfficialStatus, cancelled = page.Cancelled, endgame_reason = page.EndReason,
                    submitted = desk.MatchedActions, confirmed = desk.MatchedConfirmedActions };
                File.AppendAllText(Path.ChangeExtension(book.PathFor(batch.Id), ".ends.jsonl"),
                    JsonSerializer.Serialize(end) + Environment.NewLine);
                recordedEnd = page.Table;
            }

            switch (page.Kind)
            {
                case "dismiss":
                    await Click();
                    break;
                case "pref-saving" when !batch.QueuePending && batch.CurrentTable == null && page.Preference != null &&
                    elapsed > TimeSpan.FromSeconds(12) && reloadedPreferences.Add(page.Preference):
                    // 大厅可能已保存设置但仍显示旧值。未发起匹配时，每项只重新读取一次。
                    report(batch, "正在重新读取已保存的匹配设置。");
                    lastClick = null;
                    desk.OpenPage(lobby);
                    continue;
                case "mode" when batch.QueuePending && sawQueue && elapsed > TimeSpan.FromSeconds(10) &&
                    page.Action is "Xannaplay_pref_1631" or "wannaplay_pref_1631":
                    batch = book.Queue(batch.Id, false);
                    sawQueue = false;
                    lastClick = null;
                    break;
                case "mode" when !batch.QueuePending && batch.CurrentTable == null:
                    await Click();
                    break;
                case "join" when batch.CurrentTable == null:
                    if (!batch.QueuePending)
                    {
                        var submitted = book.WithQueueGate(batch.Id, () =>
                        {
                            if (QueueHeld()) return null;
                            batch = book.Queue(batch.Id, true);
                            return Click();
                        });
                        if (submitted == null) return;
                        if (!await submitted) batch = book.Queue(batch.Id, false);
                    }
                    else if (sawQueue && elapsed > TimeSpan.FromSeconds(10))
                    {
                        // 确实观察到在队，随后页面持续恢复可入队状态，才释放旧意向。
                        batch = book.Queue(batch.Id, false);
                        sawQueue = false;
                        lastClick = null;
                    }
                    else message = "已提交匹配，等候大厅确认入队或分桌。";
                    break;
                case "queued":
                    if (!batch.QueuePending) throw new InvalidOperationException("发现本批次之外的排队请求。");
                    sawQueue = true;
                    if (batch.OfferTable != null && elapsed > TimeSpan.FromSeconds(10))
                    {
                        batch = book.ReturnToQueue(batch.Id);
                        lastClick = null;
                        offerChecked = null;
                        acceptedAt = null;
                        tableChecks = 0;
                    }
                    break;
                case "offer":
                case "confirming":
                    if (!batch.QueuePending || page.Table == null || page.Opponent == null)
                        throw new InvalidOperationException("收到未知来源的匹配确认。");
                    if (batch.OfferTable != null && (batch.OfferTable != page.Table || batch.OpponentId != page.Opponent))
                        throw new InvalidOperationException("平台确认信息与本批匹配不一致。");
                    if (batch.OfferTable == null)
                    {
                        batch = book.AcceptOffer(batch.Id, page.Table, page.Opponent);
                        if (page.Kind == "offer" && !await Click()) { batch = book.ReturnToQueue(batch.Id); break; }
                        acceptedAt = DateTime.UtcNow;
                        acceptedHere = page.Table;
                        report(batch, $"已绑定匹配桌 {page.Table}，正在核实是否开局。");
                    }
                    break;
                case "allocated-wait" when page.Table != null && page.Table == (batch.CurrentTable ?? batch.OfferTable):
                    // 正常ready由同页单个只读请求定期刷新；只有接口缺失或请求已失效才重载原桌。
                    offerChecked ??= DateTime.UtcNow;
                    if (page.Code == "allocated-read-timeout" || page.Code == "allocated-reader-missing" &&
                        offerChecked.HasValue && DateTime.UtcNow - offerChecked > TimeSpan.FromSeconds(8))
                        CheckAllocatedTable();
                    break;
                case "started" when page.Table == (batch.CurrentTable ?? batch.OfferTable) && page.Table != null:
                    if (recoverCurrentSeat && resumedTable == page.Table && page.Code == "allocated-authority" &&
                        page.OfficialStatus == "play" && page.Host == batch.HostId && page.Opponent == batch.OpponentId)
                        verifiedResumedTable = true;
                    if (enteredAt == null || uri.AbsolutePath == "/table" &&
                        DateTime.UtcNow - enteredAt > TimeSpan.FromSeconds(4))
                    {
                        enteredAt = DateTime.UtcNow;
                        if (batch.CurrentTable == null && page.Table == batch.OfferTable && !desk.IsPlaying &&
                            page.Code == "allocated-authority" && page.OfficialStatus == "play") {
                            playVerifiedAt ??= Environment.TickCount64;
                            officialReplyAt ??= page.AuthorityAt;
                            viewOpenedAt = Environment.TickCount64;
                        }
                        desk.OpenPage(new Uri(lobby, "/tableview?table=" + page.Table));
                    }
                    break;
                case "enter":
                case "game":
                    if (page.Table == null || page.Opponent == null || page.Host != batch.HostId)
                        throw new InvalidOperationException("分配的牌桌缺少己方或对手信息。");
                    if (batch.CurrentTable == null)
                    {
                        if (!batch.QueuePending || batch.Rounds.Any(r => r.Table == page.Table))
                            throw new InvalidOperationException("未请求的牌桌或已完成的旧桌。");
                        batch = book.Attach(batch.Id, page.Table, page.Opponent);
                    }
                    if (batch.OpponentId != page.Opponent) throw new InvalidOperationException("牌桌对手已变化。");
                    if (page.Kind == "enter")
                    {
                        if (enteredAt == null) { enteredAt = DateTime.UtcNow; await Click(); }
                        else if (DateTime.UtcNow - enteredAt > TimeSpan.FromSeconds(4))
                        {
                            enteredAt = DateTime.UtcNow;
                            desk.OpenPage(new Uri(lobby, "/tableview?table=" + page.Table));
                        }
                        break;
                    }
                    if (resumedTable != null && !recoverCurrentSeat)
                        throw new InvalidOperationException("找回了中断的原桌，需通过恢复入口核实中断前的出牌。");
                    if (resumedTable != null && armed == null) {
                        if (!verifiedResumedTable || unavailableJournal) {
                            if (!verifiedResumedTable && !unavailableJournal &&
                                (!offerChecked.HasValue || DateTime.UtcNow - offerChecked.Value > TimeSpan.FromSeconds(5)))
                                CheckAllocatedTable();
                            message = "恢复仅守原桌，尚未核实原桌官方开局或原意图；需接管，保持只读。";
                            break;
                        }
                        try {
                            if (desk.StartCheckedSeat(batch.Site, page.Table, batch.HostId, page.Opponent, true)) armed = page.Table;
                        } catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or JsonException) {
                            unavailableJournal = true;
                            book.RecordIncident(batch, "resume-seat-proof-unavailable", 0);
                            message = "恢复需接管：原桌意图缺失、无法读取或与本席不匹配；保持只读。";
                            break;
                        }
                    } else if (armed == null && desk.StartMatchedSeat(batch.Site, page.Table, batch.HostId, page.Opponent))
                        armed = page.Table;
                    if (armed != null && !desk.IsPlaying)
                        throw new InvalidOperationException("即时制自动出牌已暂停。" + desk.PauseReason);
                    if (armed == null && elapsed > TimeSpan.FromSeconds(8))
                        throw new InvalidOperationException("进入原桌后未能启动己方座位。");
                    var turn = page.MyTurn == true ? page.Phase + "/" + desk.MatchedActions : null;
                    if (turn != lastTurn) { lastTurn = turn; ownTurnSince = turn == null ? null : DateTime.UtcNow; }
                    if (ownTurnSince.HasValue && DateTime.UtcNow - ownTurnSince > TimeSpan.FromSeconds(8))
                        desk.ShowMatchedTable("己方回合尚未进展，可在原桌接管");
                    message = armed == null ? "正在核对己方座位。" : page.MyTurn == true ?
                        $"己方回合 {page.Phase}，本桌已操作 {desk.MatchedActions} 次。" : $"等待对手或服务器，本桌已操作 {desk.MatchedActions} 次。";
                    break;
                case "void":
                case "non-normal":
                    if (page.Table != (batch.CurrentTable ?? batch.OfferTable))
                        throw new InvalidOperationException("异常终局桌号不属于本批次。");
                    book.StopQueue(batch.Id);
                    if (!book.RecordIncident(batch, page.Kind == "void" ? "official-void-or-skipturn" : "official-non-normal-end", desk.MatchedActions))
                        desk.ShowMatchedTable("官方异常终局无法写入磁盘，请检查本机日志目录");
                    throw new VoidedRealtimeMatchException(message);
                case "result":
                    if (desk.DrawHandoffMessage != null && page.Table == batch.CurrentTable)
                        throw new VoidedRealtimeMatchException("原桌已结算；本桌要求人工接管，自动保桌阻断，不计自动完赛。");
                    if (batch.CurrentTable == null && page.Table == batch.OfferTable) {
                        book.StopQueue(batch.Id);
                        throw new VoidedRealtimeMatchException("原匹配桌已经结束，本次没有已确认的出牌，未计入完赛。");
                    }
                    if (batch.CurrentTable == null || page.Table != batch.CurrentTable)
                        throw new InvalidOperationException("结算桌号未绑定到本批次。");
                    if (recoverCurrentSeat && resumedTable == page.Table) {
                        report(batch, "原桌已取得官方正常终局；重启救场已结束，保留原关联和意图，不计连续完赛。");
                        return;
                    }
                    if (resumedTable != null || armed == null || desk.MatchedConfirmedActions == 0)
                    {
                        book.StopQueue(batch.Id);
                        throw new VoidedRealtimeMatchException("此桌缺少本次连续运行中己方出牌后的服务端进展，已停止续排并排除完赛。");
                    }
                    try { if (!await desk.SaveVerifiedResult(batch)) break; }
                    catch (InvalidDataException invalid) { throw StopForBoundConflict(invalid.Message); }
                    var next = book.Collect(batch.Id, page.Table);
                    if (next.Completed == batch.Completed) break;
                    if (page.OfficialStatus is "finished" or "archive" && page.Cancelled == "0" && page.EndReason == "normal_end")
                        book.ReleaseSettledScoreReceipt(batch);
                    batch = next;
                    report(batch, $"第 {batch.Completed} 局已结算，桌号 {page.Table}。");
                    if (batch.Finished || StopWithoutOutstandingTable()) return;
                    lastClick = armed = resumedTable = null;
                    reloadedPreferences.Clear();
                    offerChecked = null;
                    acceptedAt = null;
                    playVerifiedAt = viewOpenedAt = officialReplyAt = null;
                    viewRetries = 0;
                    viewHoldReported = false;
                    enteredAt = ownTurnSince = null;
                    lastTurn = null;
                    tableChecks = 0;
                    sawQueue = false;
                    desk.OpenPage(lobby);
                    // 等待离开旧结算页，不能把旧页面的晚到状态当作下一桌。
                    var until = DateTime.UtcNow.AddSeconds(60);
                    while (web.Source?.AbsolutePath != "/lobby")
                    {
                        if (StopWithoutOutstandingTable()) return;
                        if (DateTime.UtcNow >= until) throw new TimeoutException("结算后未能回到大厅。");
                        await Task.Delay(150);
                    }
                    continue;
            }
            report(batch, message);
            bool allocatedExpired = batch.OfferTable != null && acceptedAt.HasValue &&
                DateTime.UtcNow - acceptedAt > TimeSpan.FromSeconds(120);
            if ((elapsed > TimeSpan.FromSeconds(120) || allocatedExpired) && page.Kind != "queued" &&
                !(page.Kind is "game" or "wait" && armed != null && desk.IsRunningMatchedSeat(batch))) {
                if (batch.OfferTable != null && page.Kind is "confirming" or "allocated-wait" or "wait" or "started" or "enter") {
                    if (!allocationHeld) {
                        allocationHeld = true;
                        try { book.StopQueue(batch.Id); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
                            report(batch, "无法保存停排记录，本进程已停止新匹配。");
                        }
                        book.RecordIncident(batch, "allocated-table-progress-unresolved", desk.MatchedActions);
                        report(batch, "原匹配桌开局仍未确认，已停止续排，保留同一监督器继续核对原桌。");
                    }
                    // 已知offer仍可能开局。可恢复确认停滞不能变成HandOver/Disarm的终点。
                    if (page.Code is not ("allocated-authority" or "allocated-reading") &&
                        !(waitingForFirstSeat && page.Code is "game-shell-empty" or "game-initializing") &&
                        (!offerChecked.HasValue || DateTime.UtcNow - offerChecked > TimeSpan.FromSeconds(8)))
                        CheckAllocatedTable();
                    continue;
                }
                throw new TimeoutException("匹配页面长时间未确认进展，已保留批次。" + message);
            }

            async Task<bool> Click()
            {
                if (lastClick == key) return true;
                using var reply = JsonDocument.Parse(await LobbyControls.Run(web, "realtime-click", key));
                if (reply.RootElement.TryGetProperty("notSubmitted", out var safe) && safe.GetBoolean()) return false;
                if (!reply.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                    throw new InvalidOperationException("未能确认匹配控件的操作。" +
                        (reply.RootElement.TryGetProperty("message", out var why) ? why.GetString() : ""));
                lastClick = key;
                return true;
            }
            void CheckAllocatedTable()
            {
                // 大厅可能丢失开局推送。用已确认的原桌号读取桌页，不再次接受或入队。
                offerChecked = DateTime.UtcNow;
                tableChecks++;
                string? original = batch.CurrentTable ?? batch.OfferTable;
                report(batch, $"正在核对已确认的匹配桌 {original}。");
                desk.OpenPage(new Uri(lobby, "/table?table=" + original + "&nr=true"));
            }
            VoidedRealtimeMatchException StopForBoundConflict(string reason)
            {
                allocationHeld = true;
                try { book.StopQueue(batch.Id); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException) {
                    reason += " 本地停排标记未能写入。";
                }
                book.RecordIncident(batch, "official-bound-table-conflict", desk.MatchedActions);
                return new VoidedRealtimeMatchException(reason + " 已保留原桌关联并停止续排。");
            }
        }
    }
}
