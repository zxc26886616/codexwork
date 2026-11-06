using System.Text.Json;

namespace SeaSaltDesk;

public sealed record SeriesRound(string Table, Seat Host, Seat Guest);
public sealed record SeriesStats(int Played, int Wins, int Ties, int Losses, double WinRate, double Average);

public sealed record SeriesState(string Id, int Planned, string HostId, string GuestName,
    string? CurrentTable, SeriesRound[] Rounds, DateTimeOffset Created, DateTimeOffset Updated)
{
    public bool Realtime { get; init; }
    public int? TurnBasedFrom { get; init; }
    public string Site { get; init; } = "BGA";
    public bool QueuePending { get; init; }
    public string? OfferTable { get; init; }
    public string? OpponentId { get; init; }
    public int Completed => Rounds.Length;
    public bool Finished => Completed == Planned;
    public SeriesStats HostStats => Stats(host: true);
    public SeriesStats GuestStats => Stats(host: false);

    private SeriesStats Stats(bool host)
    {
        int wins = 0, ties = 0;
        long points = 0;
        foreach (var round in Rounds)
        {
            var seat = host ? round.Host : round.Guest;
            points += seat.Score;
            if (round.Host.Rank == 1 && round.Guest.Rank == 1) ties++;
            else if (seat.Rank == 1) wins++;
        }
        return new(Completed, wins, ties, Completed - wins - ties,
            Completed == 0 ? 0 : (double)wins / Completed, Completed == 0 ? 0 : (double)points / Completed);
    }
}

public sealed class SeriesBook(string root)
{
    private readonly string folder = Path.GetFullPath(Path.Combine(root, "series"));
    private readonly string matches = Path.GetFullPath(Path.Combine(root, "matches"));

    public bool RecordIncident(SeriesState batch, string code, int actions, long? elapsedMs = null)
        => RecordIncident(batch, code, actions, elapsedMs, null);

    internal bool RecordIncident(SeriesState batch, string code, int actions, long? elapsedMs, object? officialTable)
    {
        try
        {
            var record = new { at = DateTimeOffset.Now, batch = batch.Id, site = batch.Site, build = typeof(SeriesBook).Assembly.ManifestModule.ModuleVersionId,
                table = batch.CurrentTable ?? batch.OfferTable, code, actions, elapsedMs };
            var data = JsonSerializer.SerializeToNode(record)!.AsObject();
            if (officialTable != null) data["officialTable"] = JsonSerializer.SerializeToNode(officialTable);
            File.AppendAllText(Path.ChangeExtension(PathFor(batch.Id), ".incidents.jsonl"), data.ToJsonString() + Environment.NewLine);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static bool NumberId(string? id) =>
        id is { Length: > 0 and <= 18 } && id[0] != '0' && id.All(char.IsAsciiDigit);

    public static bool ValidGuestName(string? name) =>
        name is { Length: >= 1 and <= 16 } && name.Trim() == name &&
        name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_');

    public string PathFor(string id)
    {
        if (id is not { Length: 32 } || !Guid.TryParseExact(id, "N", out var value))
            throw new ArgumentException("批次编号无效。");
        return Path.Combine(folder, value.ToString("N") + ".json");
    }

    public bool QueueStopped(string id) => SignalExists(Path.ChangeExtension(PathFor(id), ".stop-queue")) ||
        SignalExists(Path.ChangeExtension(PathFor(id), ".score-ack-hold"));

    private static bool SignalExists(string path)
    {
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    // 外部停排进程与出牌进程将“禁止新提交”和“派发一次入队”排序。门内只做同步派发，不等待网络回执。
    public T WithQueueGate<T>(string id, Func<T> action)
    {
        string batch = Path.GetFileNameWithoutExtension(PathFor(id));
        using var gate = new Mutex(false, @"Local\SeaSaltDesk.Queue." + batch);
        try { if (!gate.WaitOne(TimeSpan.FromSeconds(5))) throw new TimeoutException("未能取得批次入队锁。"); }
        catch (AbandonedMutexException) { /* 旧进程已退出，当前线程已取得锁。 */ }
        try { return action(); }
        finally { gate.ReleaseMutex(); }
    }

    public void StopQueue(string id) => WithQueueGate(id, () =>
    {
        if (!Load(id).Realtime) throw new InvalidOperationException("只有即时制批次可以停止续排。");
        string signal = Path.ChangeExtension(PathFor(id), ".stop-queue");
        if (SignalExists(signal)) return false;
        using var stream = new FileStream(signal, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(DateTimeOffset.UtcNow.ToString("O")));
        stream.Flush(flushToDisk: true);
        return true;
    });

    // 与人工/故障停排分开落盘。即使另一原因随后加入，解除本桌的临时标记也不会解除它。
    public void HoldScoreReceipt(SeriesState batch) => WithQueueGate(batch.Id, () =>
    {
        if (batch.CurrentTable == null || batch.OpponentId == null) throw new InvalidDataException("缺少得分确认原桌。");
        string path = Path.ChangeExtension(PathFor(batch.Id), ".score-ack-hold");
        string seat = ScoreSeat(batch);
        if (SignalExists(path))
        {
            if (File.ReadAllText(path) != seat) throw new InvalidDataException("已有其他席位的得分确认停排。");
            return false;
        }
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        stream.Write(System.Text.Encoding.UTF8.GetBytes(seat));
        stream.Flush(flushToDisk: true);
        return true;
    });

    internal void ReleaseSettledScoreReceipt(SeriesState bound) => WithQueueGate(bound.Id, () =>
    {
        string path = Path.ChangeExtension(PathFor(bound.Id), ".score-ack-hold");
        if (!SignalExists(path) || File.ReadAllText(path) != ScoreSeat(bound)) return false;
        File.Delete(path);
        return true;
    });

    private static string ScoreSeat(SeriesState batch) =>
        $"{batch.Site}/{batch.CurrentTable}/{batch.HostId}/{batch.OpponentId}";

    public SeriesState Start(int planned, string hostId, string guestName, string? id = null, string? realtimeSite = null)
    {
        if (planned is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(planned), "连续对局局数应为 1 到 1000。");
        if (!NumberId(hostId) || (realtimeSite == null ? !ValidGuestName(guestName) : realtimeSite is not ("BGA" or "Studio") || guestName != ""))
            throw new ArgumentException("请先确认房主编号与热座昵称。");
        string path = PathFor(id ?? Guid.NewGuid().ToString("N"));
        id = Path.GetFileNameWithoutExtension(path);
        if (File.Exists(path))
        {
            var existing = Load(id);
            if (existing.Planned != planned || existing.HostId != hostId || existing.GuestName != guestName ||
                existing.Realtime != (realtimeSite != null) || existing.Site != (realtimeSite ?? "BGA"))
                throw new InvalidDataException("该批次编号已用于另一组对局设置。");
            return existing;
        }
        var now = DateTimeOffset.Now;
        return Save(new SeriesState(id, planned, hostId, guestName, null, [], now, now)
            { Realtime = realtimeSite != null, Site = realtimeSite ?? "BGA" }, overwrite: false);
    }

    public SeriesState Load(string id)
    {
        string path = PathFor(id);
        var state = Read<SeriesState>(path);
        if (state == null || state.Id != Path.GetFileNameWithoutExtension(path) || !NumberId(state.HostId) ||
            (state.Realtime ? state.GuestName != "" : !ValidGuestName(state.GuestName) || state.Site != "BGA") ||
            state.Site is not ("BGA" or "Studio") || state.Planned is < 1 or > 1000 || state.Created == default ||
            state.Updated == default || state.Rounds == null || state.Completed > state.Planned)
            throw new InvalidDataException("批次文件的设置或进度无效。");
        var tables = new HashSet<string>();
        foreach (var round in state.Rounds)
        {
            if (round == null || !MatchBook.Valid(new Match("BGA", round.Table, [round.Host, round.Guest])) ||
                round.Host.Id != state.HostId || (!state.Realtime && round.Guest.Name != state.GuestName) || !tables.Add(round.Table))
                throw new InvalidDataException("批次文件中的桌号、名次或座位无效。");
        }
        if (state.CurrentTable != null && (!NumberId(state.CurrentTable) || state.Finished || tables.Contains(state.CurrentTable)))
            throw new InvalidDataException("批次文件中的进行中桌号无效。");
        if (state.TurnBasedFrom is int previous && (state.Realtime || previous < 0 || previous > state.Completed || previous >= state.Planned))
            throw new InvalidDataException("批次文件中的回合制起始局数无效。");
        if ((state.QueuePending && (!state.Realtime || state.Finished || state.CurrentTable != null)) ||
            (state.OfferTable != null && (!state.QueuePending || !NumberId(state.OfferTable) || tables.Contains(state.OfferTable))) ||
            (state.Realtime && (state.CurrentTable != null || state.OfferTable != null)
                ? !NumberId(state.OpponentId) || state.OpponentId == state.HostId : state.OpponentId != null))
            throw new InvalidDataException("批次中的排队或对手记录无效。");
        return state;
    }

    public List<SeriesState> List()
    {
        if (!Directory.Exists(folder)) return [];
        return Directory.EnumerateFiles(folder, "*.json").Select(Path.GetFileNameWithoutExtension)
            .Where(id => id?.Length == 32 && Guid.TryParseExact(id, "N", out _))
            .Select(id => Load(id!)).OrderByDescending(s => s.Updated).ToList();
    }

    public SeriesState UseTurnBased(string id)
    {
        var state = Load(id);
        if (state.Realtime || state.Finished)
            throw new InvalidOperationException("只有尚未完成的本地热座批次可以转入回合制。");
        // 已绑定的牌桌继续沿用，仅记录首次转入前的收录局数。
        return state.TurnBasedFrom.HasValue ? state :
            Save(state with { TurnBasedFrom = state.Completed, Updated = DateTimeOffset.Now });
    }

    public SeriesState Queue(string id, bool pending)
    {
        var state = Load(id);
        if (!state.Realtime || state.Finished || state.CurrentTable != null || state.QueuePending == pending)
            throw new InvalidOperationException("批次当前不能更改排队状态。");
        return Save(state with { QueuePending = pending, OfferTable = null, OpponentId = null, Updated = DateTimeOffset.Now });
    }

    public SeriesState ReturnToQueue(string id)
    {
        var state = Load(id);
        if (!state.QueuePending || state.OfferTable == null)
            throw new InvalidOperationException("没有等待确认的匹配。");
        return Save(state with { OfferTable = null, OpponentId = null, Updated = DateTimeOffset.Now });
    }

    public SeriesState AcceptOffer(string id, string table, string opponentId)
    {
        var state = Load(id);
        if (!state.QueuePending || state.OfferTable != null || !NumberId(table) || !NumberId(opponentId) ||
            state.HostId == opponentId || state.Rounds.Any(r => r.Table == table))
            throw new InvalidOperationException("匹配确认与当前排队记录不符。");
        return Save(state with { OfferTable = table, OpponentId = opponentId, Updated = DateTimeOffset.Now });
    }

    public SeriesState Attach(string id, string table, string? opponentId = null)
    {
        if (!NumberId(table)) throw new ArgumentException("桌号无效。");
        var state = Load(id);
        if (state.CurrentTable == table || state.Rounds.Any(r => r.Table == table)) return state;
        if (state.Finished || state.CurrentTable != null)
            throw new InvalidOperationException("批次已经完成，或仍有尚未收录的牌桌。");
        if (state.Realtime ? !state.QueuePending || !NumberId(opponentId) || opponentId == state.HostId : opponentId != null)
            throw new InvalidOperationException("新桌与排队记录或对手编号不符。");
        if (state.OfferTable != null && (state.OfferTable != table || state.OpponentId != opponentId))
            throw new InvalidOperationException("分到的牌桌与已接受的匹配不一致。");
        if (File.Exists(MatchPath(state.Site, table)) ||
            List().Any(s => s.Site == state.Site && s.Id != state.Id && (s.CurrentTable == table || s.Rounds.Any(r => r.Table == table))))
            throw new InvalidOperationException("该桌已有历史记录或已被其他批次使用。");
        return Save(state with { CurrentTable = table, OpponentId = opponentId, OfferTable = null, QueuePending = false, Updated = DateTimeOffset.Now });
    }

    public SeriesState Collect(string id, string table)
    {
        if (!NumberId(table)) throw new ArgumentException("桌号无效。");
        var state = Load(id);
        if (state.Rounds.Any(r => r.Table == table)) return state;
        if (state.CurrentTable != table) throw new InvalidOperationException("该桌不属于本批次当前等待的对局。");
        SavedMatch? saved;
        try { saved = Read<SavedMatch>(MatchPath(state.Site, table)); }
        catch (FileNotFoundException) { return state; }
        catch (DirectoryNotFoundException) { return state; }
        if (saved?.Match is not { Players.Length: 2 } match || match.Site != state.Site || match.Table != table || !MatchBook.Valid(match))
            throw new InvalidDataException("双人桌的战绩文件无效。");
        // 客席编号会随新桌改变。先用稳定编号认出房主，再核对另一席的真实昵称。
        var host = match.Players.FirstOrDefault(s => s.Id == state.HostId);
        var guest = match.Players.FirstOrDefault(s => s.Id != state.HostId);
        if (host == null || guest == null || (state.Realtime ? guest.Id != state.OpponentId : guest.Name != state.GuestName))
            throw new InvalidDataException("结算中的两席与批次绑定的席位不一致。");
        return Save(state with { CurrentTable = null, OpponentId = null, Updated = DateTimeOffset.Now,
            Rounds = [.. state.Rounds, new SeriesRound(table, host, guest)] });
    }

    private string MatchPath(string site, string table) => Path.Combine(matches, $"{site}-{table}.json");

    private static T? Read<T>(string path)
    {
        // UI 可以在后台替换文件时继续读完旧快照。
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try { return JsonSerializer.Deserialize<T>(stream, MatchBook.Json); }
        catch (JsonException ex) { throw new InvalidDataException("批次或战绩文件的格式无效。", ex); }
    }

    private SeriesState Save(SeriesState state, bool overwrite = true)
    {
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, MatchBook.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, PathFor(state.Id), overwrite);
            return state;
        }
        finally { File.Delete(temp); }
    }
}
