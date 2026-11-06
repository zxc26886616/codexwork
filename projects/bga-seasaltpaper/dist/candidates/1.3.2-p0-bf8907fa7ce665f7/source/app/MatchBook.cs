using System.Text.Json;

namespace SeaSaltDesk;

public sealed record Seat(string Id, string Name, int Rank, int Score);
public sealed record Match(string Site, string Table, Seat[] Players);
public sealed record SavedMatch(Match Match, DateTimeOffset Recorded);
public sealed record Summary(int Played, int Wins, int Ties, double Average);

public sealed class MatchBook(string folder)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static bool ValidId(string? id) =>
        id is { Length: > 0 and <= 18 } && id.All(char.IsAsciiDigit) && id[0] != '0';

    internal static bool Valid(Match? game) =>
        game is { Site: "BGA" or "Studio", Players.Length: >= 2 and <= 4 }
        && ValidId(game.Table)
        && game.Players.All(p => p != null && ValidId(p.Id) && !string.IsNullOrWhiteSpace(p.Name)
            && p.Rank >= 1 && p.Rank <= game.Players.Length)
        && game.Players.Any(p => p.Rank == 1)
        && game.Players.Select(p => p.Id).Distinct().Count() == game.Players.Length;

    public bool Add(Match match)
    {
        if (!Valid(match)) throw new InvalidDataException("结算名单、桌号或名次不完整。");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{match.Site}-{match.Table}.json");
        if (File.Exists(path)) return false;
        string temp = Path.Combine(folder, Guid.NewGuid() + ".tmp");
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(new SavedMatch(match, DateTimeOffset.Now), Json));
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { return false; }
            return true;
        }
        finally { File.Delete(temp); }
    }

    public List<SavedMatch> Load()
    {
        if (!Directory.Exists(folder)) return [];
        var saved = new List<SavedMatch>();
        foreach (string path in Directory.EnumerateFiles(folder, "*.json"))
        {
            var record = JsonSerializer.Deserialize<SavedMatch>(File.ReadAllText(path), Json);
            if (record == null || !Valid(record.Match)) throw new InvalidDataException($"战绩文件无效：{path}");
            saved.Add(record);
        }
        return saved.OrderByDescending(x => x.Recorded).ToList();
    }

    public static Summary ForPlayer(IEnumerable<Match> matches, string site, string playerId)
    {
        int played = 0, wins = 0, ties = 0;
        long points = 0;
        foreach (var match in matches.Where(x => x.Site == site))
        {
            var seat = match.Players.SingleOrDefault(p => p.Id == playerId);
            if (seat == null) continue;
            played++;
            points += seat.Score;
            if (seat.Rank != 1) continue;
            if (match.Players.Count(p => p.Rank == 1) > 1) ties++;
            else wins++;
        }
        return new(played, wins, ties, played == 0 ? 0 : (double)points / played);
    }
}
