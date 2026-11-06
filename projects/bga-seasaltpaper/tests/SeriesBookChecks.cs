using SeaSaltDesk;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class SeriesBookChecks
{
    public static void Run(string temp)
    {
        string root = Path.Combine(temp, "series-check");
        var book = new SeriesBook(root);
        var matches = new MatchBook(Path.Combine(root, "matches"));
        static void Require(bool ok, string reason)
        {
            if (!ok) throw new Exception(reason);
        }
        static void Reject<T>(Action action, string reason) where T : Exception
        {
            try { action(); }
            catch (T) { return; }
            throw new Exception(reason);
        }

        Reject<ArgumentOutOfRangeException>(() => book.Start(0, "10", "客席"), "允许了空批次。");
        Reject<ArgumentOutOfRangeException>(() => book.Start(1001, "10", "客席"), "允许了超过上限的批次。");
        Require(SeriesBook.ValidGuestName("客席 12-X_Y") && !SeriesBook.ValidGuestName(" 客席") &&
            !SeriesBook.ValidGuestName("客席!") && !SeriesBook.ValidGuestName(new string('字', 17)),
            "热座昵称校验与大厅规则不一致。");
        string id = Guid.NewGuid().ToString("N");
        var series = book.Start(3, "10", "客席", id);
        var other = book.Start(1000, "10", "客席");
        Require(series.Id == id && id != other.Id && book.Start(3, "10", "客席", id).Id == id, "批次创建或重试失败。");
        book.Attach(id, "101");
        Require(new SeriesBook(root).Collect(id, "101").CurrentTable == "101", "重开后丢失等待结算的桌号。");
        Reject<InvalidOperationException>(() => book.Attach(other.Id, "101"), "跨批次重复绑定了同一未结束的牌桌。");
        Reject<InvalidOperationException>(() => book.Collect(id, "999"), "批次允许收录无关桌号。");
        matches.Add(new("BGA", "101", [new("10", "房主", 1, 43), new("20", "客席", 2, 37)]));
        Require(book.Collect(id, "101").Completed == 1 && book.Collect(id, "101").Completed == 1, "重复结算被多次计数。");
        Reject<InvalidOperationException>(() => book.Attach(other.Id, "101"), "把已结束的旧桌当成了新桌。");
        book.Attach(id, "102");
        string path = book.PathFor(id);
        var legacy = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        legacy.Remove("turnBasedFrom");
        File.WriteAllText(path, legacy.ToJsonString());
        Require(book.Load(id).TurnBasedFrom == null, "未记录速度的历史批次被当作回合制。");
        var switched = book.UseTurnBased(id);
        Require(switched.TurnBasedFrom == 1 && switched.CurrentTable == "102" && switched.Rounds[0].Table == "101" &&
            book.UseTurnBased(id).Updated == switched.Updated, "转入回合制丢失原桌或重复切换改写了批次。");
        Require(book.Collect(id, "101").CurrentTable == "102", "重放上一桌的结算清除了新桌。");
        matches.Add(new("BGA", "102", [new("21", "客席", 1, 36), new("10", "房主", 2, 27)]));
        book.Collect(id, "102");
        Require(book.UseTurnBased(id).TurnBasedFrom == 1, "收录下一局后转入边界改变。");
        book.Attach(id, "103");
        matches.Add(new("BGA", "103", [new("10", "房主", 1, 35), new("22", "客席", 1, 35)]));
        book.Collect(id, "103");

        var done = new SeriesBook(root).Load(id);
        Reject<InvalidOperationException>(() => book.UseTurnBased(id), "已完成批次可以切换速度。");
        Require(done.Finished && done.CurrentTable == null && done.Completed == 3 &&
            done.HostStats == new SeriesStats(3, 1, 1, 1, 1d / 3, 35) &&
            done.GuestStats == new SeriesStats(3, 1, 1, 1, 1d / 3, 36) &&
            done.Rounds.Select(r => r.Guest.Id).SequenceEqual(["20", "21", "22"]),
            "热座跨桌更换编号后的独胜、并列、败局、胜率或均分有误。");
        book.Attach(other.Id, "104");
        matches.Add(new("BGA", "104", [new("10", "房主", 1, 30), new("23", "另一位玩家", 2, 22)]));
        Reject<InvalidDataException>(() => book.Collect(other.Id, "104"), "客席昵称不符仍然入了批次。");
        File.WriteAllText(Path.ChangeExtension(book.PathFor(id), ".status.json"), "{}");
        Require(book.List().Count == 2 && book.Load(other.Id).CurrentTable == "104" && book.Load(other.Id).Completed == 0,
            "异常结算或后台状态文件污染了批次记录。");
        var realtime = book.Start(3, "10", "", realtimeSite: "Studio");
        Reject<InvalidOperationException>(() => book.UseTurnBased(realtime.Id), "真人即时批次可以转为热座。");
        foreach (var invalid in new[] { done with { TurnBasedFrom = -1 }, done with { TurnBasedFrom = done.Planned },
            other with { TurnBasedFrom = 1 }, realtime with { TurnBasedFrom = 0 } })
        {
            string invalidPath = book.PathFor(invalid.Id);
            string original = File.ReadAllText(invalidPath);
            try
            {
                File.WriteAllText(invalidPath, JsonSerializer.Serialize(invalid, MatchBook.Json));
                Reject<InvalidDataException>(() => book.Load(invalid.Id), "接受了无效的回合制起始局数。");
            }
            finally { File.WriteAllText(invalidPath, original); }
        }
        Console.WriteLine("通过 · 连续对局的桌号绑定、结算与恢复、客席换号、回合制历史分段和批次统计。");
    }
}
