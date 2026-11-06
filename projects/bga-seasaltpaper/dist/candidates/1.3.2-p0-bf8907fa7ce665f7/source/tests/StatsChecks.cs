using SeaSaltDesk;

internal static class StatsChecks
{
    public static async Task Run(TableBrowser browser, string folder)
    {
        // 沿用受控桌已经打开的结算页。四人先显示首尾两行，随后补全中间两位。
        const string setup = """
            (() => {
                const panel = document.getElementById('game_result');
                window.savedResultRows = panel.innerHTML;
                panel.replaceChildren();
                const stats = document.createElement('div');
                stats.id = 'stats-check-roster';
                panel.after(stats);
                for (const id of [10, 20, 30, 40]) {
                    const header = document.createElement('span');
                    header.id = 'playerstatheader_' + id;
                    stats.append(header);
                }
                window.addScoreRow = (id, name, rank, score, last = false) => {
                    const row = document.createElement('div');
                    row.id = 'score_entry_' + id;
                    row.className = 'score-entry' + (last ? ' last-score-entry' : '');
                    row.innerHTML = '<div class="rank">' + rank + '</div><a class="playername">' + name +
                        '</a><div class="score">' + score + ' <i></i><span class="score_aux">(8)</span></div>';
                    panel.append(row);
                };
                addScoreRow(10, '甲', '第1名', 35);
                addScoreRow(40, '丁', '第4名', 6, true);
            })()
            """;
        await browser.CoreWebView2.ExecuteScriptAsync(setup);
        try
        {
            if ((await browser.ReadAsync()).Kind != "wait")
                throw new Exception("四人桌仍缺两位玩家时不应收录战绩。");
            await browser.CoreWebView2.ExecuteScriptAsync(
                "addScoreRow(20,'乙','第1名',35); addScoreRow(30,'丙','第3名',29);" +
                "document.querySelector('.last-score-entry').classList.remove('last-score-entry');");
            if ((await browser.ReadAsync()).Kind != "wait")
                throw new Exception("缺少结算最后一行的完成标记时不应收录战绩。");
            await browser.CoreWebView2.ExecuteScriptAsync("""
                document.querySelector('#score_entry_40').classList.add('last-score-entry');
                document.querySelector('#score_entry_40 .playername').outerHTML = '<div class="name"><strong id="player_40_name">丁</strong> (热座玩家)</div>';
                """);
            var read = await browser.ReadAsync();
            if (read.Kind != "result" || read.Result is not { } match ||
                !match.Players.OrderBy(s => s.Id).SequenceEqual([
                    new Seat("10", "甲", 1, 35), new Seat("20", "乙", 1, 35),
                    new Seat("30", "丙", 3, 29), new Seat("40", "丁", 4, 6)]))
                throw new Exception("四人结算的并列名次或主分数有误。");
            var writes = await Task.WhenAll(Enumerable.Range(0, 8)
                .Select(_ => Task.Run(() => new MatchBook(folder).Add(match))));
            var games = new MatchBook(folder).Load().Select(row => row.Match).ToList();
            if (writes.Count(added => added) != 1 || games.Count != 1 ||
                MatchBook.ForPlayer(games, match.Site, "10") != new Summary(1, 0, 1, 35) ||
                MatchBook.ForPlayer(games, match.Site, "30") != new Summary(1, 0, 0, 29))
                throw new Exception("并发收录同桌或四人桌的玩家统计有误。");
            Console.WriteLine("通过 · 四人结算缺行等待、并列第一、主分数与并发去重");
        }
        finally
        {
            await browser.CoreWebView2.ExecuteScriptAsync(
                "document.getElementById('game_result').innerHTML=window.savedResultRows;" +
                "document.getElementById('stats-check-roster').remove(); delete window.savedResultRows; delete window.addScoreRow;");
        }
    }
}
