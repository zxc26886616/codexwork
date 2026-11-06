using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace SeaSaltDesk;

internal sealed class SeriesDialog : Form
{
    private readonly string root;
    private readonly SeriesBook book;
    private readonly NumericUpDown count = new() { Minimum = 1, Maximum = 1000, Value = 10, Width = 100 };
    private readonly CheckBox turnBased = new() { Text = "BGA 回合制（本地热座）", AutoSize = true };
    private readonly ComboBox batches = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
    private readonly TextBox guest = new() { Text = "FoldTest", Width = 190, MaxLength = 16 };
    private readonly Label progress = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly Label scores = new() { AutoSize = true };
    private readonly Label notice = new() { AutoSize = true, MaximumSize = new Size(500, 0) };
    private readonly TextBox recentLog = new() { Width = 500, Height = 140, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Vertical, TabStop = false, AccessibleName = "最近批次操作" };
    private readonly Button start = new() { Text = "开始新批次", AutoSize = true };
    private readonly Button realtime = new() { Text = "即时制连打 3 局", AutoSize = true };
    private readonly Button resume = new() { Text = "继续未完成批次", AutoSize = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1000 };
    private sealed record Worker(Process Process, bool TurnBased);
    private readonly Dictionary<string, Worker> workers = [];
    private string? currentId;
    private DateTime lastUpdated;
    private long logLength = -1;
    private DateTime logUpdated;
    private SeriesState? current;

    private sealed record RunStatus(string BatchId, string Status, string Message, string? Table, int Completed, int Planned);

    public SeriesDialog(string root)
    {
        this.root = root;
        book = new(root);
        Text = "连续对局";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(15), AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2 };
        Controls.Add(panel);
        panel.Controls.Add(new Label { Text = "计划局数", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        panel.Controls.Add(count, 1, 0);
        panel.Controls.Add(new Label { Text = "本地热座名", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        panel.Controls.Add(guest, 1, 1);
        panel.Controls.Add(new Label { Text = "热座速度", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        panel.Controls.Add(turnBased, 1, 2);
        panel.Controls.Add(new Label { Text = "查看批次", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        panel.Controls.Add(batches, 1, 3);
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var close = new Button { Text = "关闭", AutoSize = true, DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([start, realtime, resume, close]);
        var sections = new Control[] {
            new Label { Text = "热座按计划局数运行。即时制使用已保存的站点，自动匹配三局。", AutoSize = true },
            actions, progress, scores, notice, new Label { Text = "最近批次操作", AutoSize = true }, recentLog
        };
        for (int i = 0; i < sections.Length; i++)
        {
            sections[i].Margin = new Padding(3, 6, 3, 3);
            panel.Controls.Add(sections[i], 0, i + 4);
            panel.SetColumnSpan(sections[i], 2);
        }
        CancelButton = close;
        start.Click += (_, _) => Launch(false);
        realtime.Click += (_, _) => Launch(false, match: true);
        resume.Click += (_, _) => Launch(true);
        turnBased.CheckedChanged += (_, _) => RefreshBatch();
        batches.SelectionChangeCommitted += (_, _) => RefreshBatch();
        timer.Tick += (_, _) => RefreshBatch();
        VisibleChanged += (_, _) =>
        {
            timer.Enabled = Visible;
            if (Visible) RefreshBatch();
        };
        Disposed += (_, _) => { timer.Dispose(); foreach (var worker in workers.Values) worker.Process.Dispose(); };
    }

    private static bool Locked(string path)
    {
        try
        {
            using var probe = File.Open(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33) { return true; }
    }

    private string? SelectedBatch(string? selectId)
    {
        var folder = new DirectoryInfo(Path.Combine(root, "series"));
        // 批次统计和运行状态由不同文件原子写入。进程刚建档时可能还没有状态文件。
        var saved = folder.Exists ? folder.EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => f.Name.Split('.')[0]).Where(id => Guid.TryParseExact(id, "N", out _)) : [];
        var ids = workers.Keys.Concat(saved).Distinct().ToArray();
        string? selected = selectId ?? batches.SelectedItem as string;
        // 两条执行通道可同时更新。保持用户正在查看的批次，避免日志每次刷新就跳到另一桌。
        if (ids.Length != batches.Items.Count || ids.Any(id => !batches.Items.Contains(id)))
        {
            batches.Items.Clear();
            batches.Items.AddRange(ids);
        }
        if (selected == null || !batches.Items.Contains(selected)) selected = ids.FirstOrDefault();
        if (selected != batches.SelectedItem as string) batches.SelectedItem = selected;
        return selected;
    }

    private void RefreshLog(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) { logLength = -1; recentLog.Clear(); return; }
            if (file.Length == logLength && file.LastWriteTimeUtc == logUpdated) return;
            const int tailBytes = 16 * 1024;
            using var stream = file.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // 批次可运行上千局。仅取尾部，并跳过半截 UTF-8 行。
            long offset = Math.Max(0, file.Length - tailBytes);
            stream.Position = offset;
            byte[] tail = new byte[file.Length - offset];
            stream.ReadExactly(tail);
            int first = offset == 0 ? 0 : Array.IndexOf(tail, (byte)'\n') + 1;
            string text = offset > 0 && first == 0 ? "" : System.Text.Encoding.UTF8.GetString(tail, first, tail.Length - first);
            recentLog.Lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).TakeLast(15).ToArray();
            recentLog.SelectionStart = recentLog.TextLength;
            recentLog.ScrollToCaret();
            logLength = file.Length;
            logUpdated = file.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logLength = -1;
            recentLog.Text = "暂时无法读取此批次的操作日志。";
        }
    }

    private void RefreshBatch(string? selectId = null)
    {
        try
        {
            foreach (var (key, worker) in workers.ToArray())
            {
                if (!worker.Process.HasExited) continue;
                if (worker.Process.ExitCode == 3) notice.Text = "该批次或所选速度已有任务运行，请查看已有批次。";
                else if (worker.Process.ExitCode != 0) notice.Text = "后台未完成本次请求，请查看批次状态和停止原因。";
                worker.Process.Dispose();
                workers.Remove(key);
            }
            bool immediateBusy = workers.Values.Any(w => !w.TurnBased) || Locked(Path.Combine(root, "series-run.lock"));
            bool turnBasedBusy = workers.Values.Any(w => w.TurnBased) || Locked(Path.Combine(root, "turnbased-run.lock"));
            string? id = SelectedBatch(selectId);
            bool active = id != null && (workers.ContainsKey(id) || Locked(Path.ChangeExtension(book.PathFor(id), ".run.lock")));
            if (currentId != id)
            {
                current = null;
                lastUpdated = default;
                currentId = id;
                logLength = -1;
                recentLog.Clear();
            }
            RunStatus? state = null;
            if (id != null)
            {
                string path = book.PathFor(id);
                RefreshLog(Path.ChangeExtension(path, ".log"));
                if (File.Exists(path))
                {
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (current == null || stamp != lastUpdated) { current = book.Load(id); lastUpdated = stamp; }
                }
                string statusFile = Path.ChangeExtension(path, ".status.json");
                if (File.Exists(statusFile))
                {
                    using var stream = File.Open(statusFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    state = JsonSerializer.Deserialize<RunStatus>(stream, MatchBook.Json);
                    if (state?.BatchId != id) throw new InvalidDataException();
                }
            }
            realtime.Enabled = !immediateBusy;
            start.Enabled = count.Enabled = guest.Enabled = !(turnBased.Checked ? turnBasedBusy : immediateBusy);
            bool resumeTurnBased = current is { Realtime: false } && (current.TurnBasedFrom.HasValue || turnBased.Checked);
            resume.Enabled = !active && current is { Finished: false } && !(resumeTurnBased ? turnBasedBusy : immediateBusy);
            int planned = current?.Planned ?? state?.Planned ?? (int)count.Value;
            int completed = current?.Completed ?? state?.Completed ?? 0;
            string stage = current?.Finished == true || state?.Status == "completed" ? "已完成" :
                    state?.Status is "failed" or "stopped" ? "已停止" : active ? "运行中" : "已中断";
            string? table = state?.Table ?? current?.CurrentTable;
            progress.Text = id == null ? (active ? "已有后台批次正在启动。" : "尚无连续对局记录。") :
                $"{stage}　已完成 {completed} / {planned} 局" + (string.IsNullOrEmpty(table) ? "" : $"　桌号 {table}");
            if (current?.TurnBasedFrom is int previous)
                progress.Text += $"{Environment.NewLine}BGA 回合制　转入前已收录 {previous} 局，转入后已收录 {completed - previous} 局。";
            else if (current != null)
                progress.Text += current.Realtime ? "　真人即时制" : "　本地热座";
            if (state != null && (state.Status != "running" || active) && !string.IsNullOrWhiteSpace(state.Message))
                progress.Text += Environment.NewLine + state.Message;
            if (current is { Completed: > 0 } batch)
            {
                var hostStats = batch.HostStats;
                var guestStats = batch.GuestStats;
                var other = batch.Realtime ? "匹配对手" : $"热座 {batch.GuestName}";
                scores.Text = $"己方　独胜率 {hostStats.WinRate:P1}　均分 {hostStats.Average:F1}" +
                    Environment.NewLine + $"{other}　独胜率 {guestStats.WinRate:P1}　均分 {guestStats.Average:F1}";
            }
            else scores.Text = id == null ? "" : "首局结算后显示双方的独胜率和均分。";
        }
        catch (Exception)
        {
            realtime.Enabled = start.Enabled = resume.Enabled = false;
            progress.Text = "暂时无法读取批次状态，请检查本机资料目录。";
            scores.Text = "";
        }
    }

    private void Launch(bool continuing, bool match = false)
    {
        RefreshBatch();
        if (!(continuing ? resume.Enabled : match ? realtime.Enabled : start.Enabled))
        {
            notice.Text = "当前无法启动，请查看批次状态。";
            return;
        }
        string name = guest.Text.Trim();
        if (!continuing && !match && !SeriesBook.ValidGuestName(name))
        {
            notice.Text = "请输入 1 到 16 个字的热座名，可使用文字、数字、空格、横线或下划线。";
            return;
        }
        string exe = Path.Combine(AppContext.BaseDirectory, "SeatControl.exe");
        if (!File.Exists(exe))
        {
            notice.Text = "未找到后台程序。请从包含 SeatControl.exe 的同版本发布目录启动桌面助手。";
            return;
        }
        string id = continuing ? currentId! : Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        string[] args = continuing ? ["--resume", id] : match ?
            ["--realtime", "3", "--site", (new LoginVault(root).Preferred ?? LoginSite.Studio).ToString(), "--batch-id", id] :
            ["--series", ((int)count.Value).ToString(CultureInfo.InvariantCulture), "--guest", name, "--batch-id", id];
        foreach (string arg in args) info.ArgumentList.Add(arg);
        bool runTurnBased = continuing ? current is { Realtime: false } && (turnBased.Checked || current.TurnBasedFrom.HasValue) :
            !match && turnBased.Checked;
        if (runTurnBased) { info.ArgumentList.Add("--speed"); info.ArgumentList.Add("turnbased"); }
        info.ArgumentList.Add("--root");
        info.ArgumentList.Add(root);
        try
        {
            var process = Process.Start(info) ?? throw new InvalidOperationException();
            workers.Add(id, new Worker(process, runTurnBased));
            notice.Text = "";
            RefreshBatch(id);
        }
        catch (Exception) { notice.Text = "无法启动后台进程，请检查发布目录和文件权限。"; }
    }
}
