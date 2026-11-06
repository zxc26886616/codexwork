namespace SeaSaltDesk;

public sealed partial class Desk : Form
{
    private TableBrowser browser = new() { Dock = DockStyle.Fill };
    private readonly MatchBook book;
    private readonly LoginVault vault;
    private LoginAttempt? signingIn;
    private string? loginNotice;
    private readonly TextBox address = new() { Dock = DockStyle.Fill, PlaceholderText = "粘贴 BGA 链接或桌号" };
    private readonly Button toggle = new() { Text = "启动自动出牌", AutoSize = true, Enabled = false };
    private readonly OperationLogPane operationLog = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Name = "status", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 900 };
    private readonly ComboBox players = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 245 };
    private readonly Label totals = new() { AutoSize = true, Padding = new Padding(7) };
    private readonly DataGridView grid = new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        BackgroundColor = Color.White, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
    private List<SavedMatch> matches = [];
    private string? armed, ready, recorded, pauseReason;
    private Uri? armedAddress;
    private string? unconfirmedPhase;
    private int navigation;
    private readonly Func<Task> replaceHungBrowser;
    internal bool ReplacingBrowser { get; private set; }
    public event Action<TableBrowser>? BrowserCreated;
    private bool busy;
    private int tickOwner;
    private long tickStarted, nextProbe, pendingSince;
    private long? ownDeadline, interfaceWaitSince, pendingRecoveryDeadline;
    private long lastClockAudit;
    private string? lastClockAuditState;
    private bool clockAuditFailed;
    private bool reconnect, loadingRecovery, platformPenaltyPage;
    internal string? MatchedPenaltyNotice { get; private set; }
    private ulong? tableNavigation;
    private readonly string seatFolder;
    private string? journalPath;
    private sealed record Intent(string Id, string Table, string Opponent, int Generation, SeatPlan Plan,
        string? PhaseSource = null, long? OpponentMove = null, OpponentTurn? FirstHandoff = null,
        DateTimeOffset? RegressedDrawAt = null, Intent? OriginalDraw = null, bool DrawHandoff = false);
    private string? projectionIntent;
    private bool checkingPhaseChange;
    private long projectionDeadline;
    private string? lastProjection;
    private Intent? pending;
    private string? recoveredIntent;
    private TaskCompletionSource revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task ControllerRevoked => revoked.Task;
    private static long Now => Environment.TickCount64;
    private readonly bool background;
    private bool handoverVisible;
    public TableBrowser GameBrowser => browser;
    public string? PauseReason => pauseReason;
    public bool IsPlaying => armed != null;
    public string? DrawHandoffMessage { get; private set; }
    public int MatchedActions { get; private set; }
    public int MatchedConfirmedActions { get; private set; }
    private string? lastConfirmedIntent;
    private bool batchVerifiesResults;
    internal void RequireVerifiedResults() => batchVerifiesResults = true;
    public void OpenPage(Uri uri) => Navigate(uri);

    private string? matchedOpponent;
    public bool StartMatchedSeat(string site, string table, string self, string opponent)
        => StartCheckedSeat(site, table, self, opponent, false);

    private static bool HasProgress(string path, string intent)
    {
        string ledger = Path.ChangeExtension(path, ".progress.jsonl");
        return File.Exists(ledger) && File.ReadLines(ledger).Any(line => {
            using var entry = System.Text.Json.JsonDocument.Parse(line);
            return entry.RootElement.TryGetProperty("Id", out var id) && id.GetString() == intent;
        });
    }

    internal bool StartCheckedSeat(string site, string table, string self, string opponent, bool recovering)
    {
        if (ready != $"{site}/{table}/座位/{self}" || !toggle.Enabled || armed != null) return false;
        var path = Path.Combine(seatFolder, $"{site}-{table}-{self}.json");
        // 先核对已有记录。不能因浏览器丢了回执或窗口重开，就丢弃原桌的在途动作。
        var saved = File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<Intent>(File.ReadAllText(path), MatchBook.Json) : null;
        if (File.Exists(path) && (saved == null || saved.Table != ready || saved.Opponent != opponent || saved.Plan == null))
            throw new InvalidDataException("原桌待确认动作与当前席位不一致。");
        if (recovering && (saved == null || !Guid.TryParseExact(saved.Id, "N", out _) || saved.PhaseSource != "last-server-state" ||
            !saved.Plan.OfficialOwn || saved.Plan.Move is null or < 0 || string.IsNullOrEmpty(saved.Plan.DocumentId) ||
            string.IsNullOrWhiteSpace(saved.Plan.Phase) || string.IsNullOrWhiteSpace(saved.Plan.Target) ||
            string.IsNullOrWhiteSpace(saved.Plan.Board) ||
            System.Text.Json.JsonSerializer.Deserialize<string[][]>(saved.Plan.Board) is not { Length:6 } zones ||
            zones.Any(zone => zone == null) ||
            saved.Plan.Local != "" || saved.DrawHandoff || saved.RegressedDrawAt != null || saved.OriginalDraw != null))
            throw new InvalidDataException("原桌没有足以跨进程核对的意图。");
        journalPath = path;
        if (saved?.FirstHandoff is { } handoff)
        {
            // 交接事实与最高行动号已原子持久化。崩溃若发生在追加审计前，重开时补齐一次。
            if (!HasProgress(path, saved.Id)) RecordProgress(saved, handoff.Phase, handoff.Move, null, false, saved.PhaseSource, "opponent-turn", false);
        }
        pending = saved;
        recoveredIntent = recovering ? saved!.Id : null;
        DrawHandoffMessage = saved?.DrawHandoff == true ? DrawHandoffNotice(table) : null;
        pendingSince = Now;
        matchedOpponent = opponent;
        MatchedPenaltyNotice = null;
        MatchedActions = 0;
        MatchedConfirmedActions = 0;
        lastConfirmedIntent = recovering && HasProgress(path, saved!.Id) ? saved.Id : null;
        toggle.PerformClick();
        return armed == ready;
    }

    public void ShowMatchedTable(string message)
    {
        if (Text == "海盐折纸 · " + message) return;
        // 后台故障通过状态和日志报告，继续保留透明、屏幕外及 NOACTIVATE 的窗口属性。
        Text = "海盐折纸 · " + message;
        ShowStatus(message);
    }

    public void HandOverMatchedTable(Uri table, string reason)
    {
        Disarm();
        var current = browser.Source;
        if (current == null || current.AbsolutePath != "/tableview" ||
            current.GetLeftPart(UriPartial.Authority) != table.GetLeftPart(UriPartial.Authority) || current.Query != table.Query)
            Navigate(table);
        ShowMatchedTable("需要接管原桌 · " + reason);
        if (background) return;
        handoverVisible = true;
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        Activate();
    }

    public bool IsRunningMatchedSeat(SeriesState batch) => batch.CurrentTable != null && batch.OpponentId != null &&
        matchedOpponent == batch.OpponentId && armed == $"{batch.Site}/{batch.CurrentTable}/座位/{batch.HostId}";

    internal async Task<bool> SaveVerifiedResult(SeriesState batch)
    {
        var read = await browser.ReadAsync();
        if (read.Result == null || read.Kind != "result") return false;
        var m = read.Result;
        if (m.Site != batch.Site || m.Table != batch.CurrentTable || m.Players.Length != 2 ||
            !m.Players.Any(p => p.Id == batch.HostId) || !m.Players.Any(p => p.Id == batch.OpponentId))
            throw new InvalidDataException("官方结算的桌号或双方席位与批次不符。");
        StoreResult(m);
        // 已复核双方席位与正式结算，释放这桌最后一笔待确认意图。
        var path = Path.Combine(seatFolder, $"{m.Site}-{m.Table}-{batch.HostId}.json");
        if (File.Exists(path)) File.Delete(path);
        return true;
    }

    private void StoreResult(Match match)
    {
        bool added = book.Add(match);
        ShowStatus($"桌号 {match.Table} · {(added ? "战绩已保存" : "战绩已存在")}，可在「对局统计」查看。");
        RefreshHistory();
    }

    public bool StartTwoLocalSeats(string table, string host)
    {
        var parts = ready?.Split('/');
        if (parts is not ["BGA", var number, "热座", var owner, var seats] || number != table || owner != host)
            return false;
        var ids = seats.Split('-');
        if (ids.Length != 2 || ids[0] == ids[1] || !ids.Contains(host)) return false;
        if (armed == null && toggle.Enabled) toggle.PerformClick();
        return armed == ready;
    }

    public event Action<string>? LogLine;
    public event Action<string, string>? MatchedReceiptUnresolved;
    public string? UnresolvedScoreIntent { get; private set; }
    private string? reportedUnresolvedIntent;

    protected override bool ShowWithoutActivation => (background && !handoverVisible) || base.ShowWithoutActivation;
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            if (background && !handoverVisible) cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    private sealed record PlayerChoice(string Site, string Id, string Title)
    {
        public override string ToString() => Title;
    }

    public Desk(string root, Uri? firstPage = null) : this(root, firstPage, false) { }

    public Desk(string root, Uri? firstPage, bool background)
    {
        this.background = background;
        seatFolder = Path.Combine(root, "seat-actions");
        vault = new(root);
        book = new MatchBook(Path.Combine(root, "matches"));
        Text = "海盐折纸 · BGA 桌面助手";
        // 后台牌桌留出桌面版大厅所需的宽度，避免组队浮窗遮住游戏控件。
        ClientSize = background ? new Size(1680, 1000) : new Size(1180, 790);
        MinimumSize = new Size(820, 540);
        if (background)
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(SystemInformation.VirtualScreen.Left - Width - 64, SystemInformation.VirtualScreen.Top);
            ShowInTaskbar = false;
            Opacity = 0;
        }
        Font = new Font("Microsoft YaHei UI", 9F);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        var table = new TabPage("牌桌");
        var history = new TabPage("对局统计");
        tabs.TabPages.AddRange([table, history]);
        Controls.Add(tabs);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 2 };
        layout.ColumnStyles.Add(new(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new(SizeType.Absolute, 250));
        layout.RowStyles.Add(new(SizeType.AutoSize));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 34));
        table.Controls.Add(layout);
        var bar = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Padding = new Padding(4), ColumnCount = 6 };
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        bar.ColumnStyles.Add(new(SizeType.AutoSize));
        Button Button(string title, Action action)
        {
            var b = new Button { Text = title, AutoSize = true };
            b.Click += (_, _) => action();
            return b;
        }
        void OpenAddress()
        {
            try { Navigate(TableBrowser.Address(address.Text)); }
            catch (ArgumentException) { ShowStatus("请输入正确的 BGA 链接或纯数字桌号。"); }
        }
        bar.Controls.Add(Button("游戏首页", () => Navigate(new(TableBrowser.Panel))), 0, 0);
        bar.Controls.Add(Button("单人教程", () => Navigate(new(TableBrowser.Tutorial))), 1, 0);
        bar.Controls.Add(address, 2, 0);
        bar.Controls.Add(Button("打开", OpenAddress), 3, 0);
        bar.Controls.Add(toggle, 4, 0);
        var loginButton = Button("自动登录", () =>
        {
            signingIn = null;
            loginNotice = null;
            try
            {
                using var dialog = new LoginSettings(vault);
                if (dialog.ShowDialog(this) == DialogResult.OK) BeginLogin(dialog.SelectedSite);
            }
            catch (Exception) { ShowStatus("无法读取自动登录设置，请检查本机保存的登录资料。"); }
        });
        loginButton.Enabled = false;
        bar.Controls.Add(loginButton, 5, 0);
        if (!background)
        {
            SeriesDialog? series = null;
            bar.ColumnCount++;
            bar.ColumnStyles.Add(new(SizeType.AutoSize));
            bar.Controls.Add(Button("连续对局", () =>
            {
                series ??= new SeriesDialog(root);
                series.ShowDialog(this);
            }), 6, 0);
            Disposed += (_, _) => series?.Dispose();
        }
        layout.Controls.Add(bar, 0, 0);
        layout.SetColumnSpan(bar, 2);
        layout.Controls.Add(browser, 0, 1);
        layout.Controls.Add(operationLog, 1, 1);
        layout.Controls.Add(status, 0, 2);
        layout.SetColumnSpan(status, 2);
        address.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenAddress(); } };
        toggle.Click += (_, _) =>
        {
            armed = armed == null ? ready : null;
            unconfirmedPhase = null;
            armedAddress = armed == null ? null : browser.Source;
            pauseReason = armed == null ? "已暂停，可手动操作。" : null;
            toggle.Text = armed == null ? "启动自动出牌" : "暂停自动出牌";
            ShowStatus(armed == null ? "已暂停，可手动操作。" : "已启动自动出牌。", action: true);
        };
        var filters = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 48, Padding = new Padding(5) };
        filters.Controls.AddRange([players, totals]);
        foreach (var (id, title) in new[] { ("time", "收录时间"), ("site", "来源"), ("table", "桌号"),
            ("result", "最终名次与分数") }) grid.Columns.Add(id, title);
        grid.Columns["result"]!.FillWeight = 300;
        history.Controls.Add(grid);
        history.Controls.Add(filters);
        players.SelectedIndexChanged += (_, _) => RenderMatches();
        tabs.SelectedIndexChanged += (_, _) => { if (tabs.SelectedTab == history) RefreshHistory(); };
        async Task SetupBrowser()
        {
            var current = browser;
            await browser.InitializeAsync(Path.Combine(root, "profile"));
            if (background)
            {
                var core = browser.CoreWebView2;
                core.Settings.AreDefaultScriptDialogsEnabled = false;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
                core.PermissionRequested += (_, e) => e.State = Microsoft.Web.WebView2.Core.CoreWebView2PermissionState.Deny;
                core.BasicAuthenticationRequested += (_, e) => e.Cancel = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;
            }
            browser.CoreWebView2.ProcessFailed += (_, e) =>
            {
                if (current != browser || current.IsDisposed) return;
                if (armed == null || matchedOpponent == null) return;
                if (e.ProcessFailedKind is Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.RenderProcessExited or
                    Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.FrameRenderProcessExited)
                    RecoverTable("渲染进程已退出，撤销旧文档并重新核对原桌。");
                // Unresponsive 可能只是同步读取较慢。独立消息泵定时器检查阻塞是否持续。
            };
            browser.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (current != browser || current.IsDisposed) return;
                if (fallback is { PrearmStarted: > 0, Bound:false } guard && e.IsSuccess && current.Source is { } loaded &&
                    loaded.AbsolutePath is "/table" or "/tableview" && loaded.GetLeftPart(UriPartial.Authority) == guard.Lobby.GetLeftPart(UriPartial.Authority) &&
                    System.Web.HttpUtility.ParseQueryString(loaded.Query)["table"] == guard.Table)
                {
                    int revision = navigation;
                    BeginInvoke(new Action(async () => { if (!IsDisposed && !current.IsDisposed && current == browser &&
                        fallback == guard && !guard.Bound && revision == navigation) await TickAsync(); }));
                }
                if (e.NavigationId != tableNavigation) return;
                tableNavigation = null;
                loadingRecovery = !e.IsSuccess || e.HttpStatusCode >= 400;
                nextProbe = Now + 2500;
            };
            browser.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (current != browser || current.IsDisposed) return;
                // 导航开始即使旧文档观察失效，不能用跨页晚到的 game 回执判断新页的控制器状态。
                revoked.TrySetResult();
                revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
                navigation++;
                ownDeadline = interfaceWaitSince = pendingRecoveryDeadline = null;
                Uri.TryCreate(e.Uri, UriKind.Absolute, out var target);
                bool sameTable = armed != null && armedAddress != null && target != null &&
                    Uri.Compare(armedAddress, target, UriComponents.SchemeAndServer | UriComponents.PathAndQuery,
                        UriFormat.UriEscaped, StringComparison.Ordinal) == 0;
                // 平台处罚提醒不证明原桌已结束。保留原席位及在途意图，只能重读已绑定原桌。
                platformPenaltyPage = armed != null && matchedOpponent != null && armedAddress != null &&
                    target?.AbsolutePath == "/penalty" && target.GetLeftPart(UriPartial.Authority) ==
                    armedAddress.GetLeftPart(UriPartial.Authority);
                if (platformPenaltyPage)
                {
                    MatchedPenaltyNotice = "平台打开了处罚提醒页，请核实页面提示（" +
                        target!.GetLeftPart(UriPartial.Authority) + "/penalty）。";
                    loadingRecovery = false;
                    nextProbe = Now + 2500;
                }
                if (!sameTable && !platformPenaltyPage) Disarm();
                else if (matchedOpponent != null) tableNavigation = e.NavigationId;
                pauseReason = null;
                loginNotice = null;
                recorded = null;
                ShowStatus(sameTable ? "牌桌重新连接，正在核对桌号与座位。" : "正在载入页面…");
            };
            browser.CoreWebView2.SourceChanged += (_, _) => { if (current == browser && !current.IsDisposed) address.Text = current.Source?.AbsoluteUri ?? ""; };
        }
        replaceHungBrowser = async () =>
        {
            var target = ControllerRecoveryAddress;
            if (ReplacingBrowser || target == null) return;
            ReplacingBrowser = true;
            ++navigation;
            ++tickOwner;
            busy = false;
            revoked.TrySetResult();
            revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
            ShowStatus("原桌脚本持续无响应，正在关闭旧出牌文档。");
            var old = browser;
            try
            {
                var env = old.CoreWebView2.Environment;
                uint oldFrame = old.CoreWebView2.FrameId;
                using var oldProcess = System.Diagnostics.Process.GetProcessById((int)old.CoreWebView2.BrowserProcessId);
                _ = oldProcess.SafeHandle;
                // 先关闭本窗口拥有的 WebView。只观察其旧帧是否仍活跃，不操作系统进程。
                layout.Controls.Remove(old);
                old.Dispose();
                while (!IsDisposed)
                {
                    try
                    {
                        if (oldProcess.HasExited) break;
                        var infos = await env.GetProcessExtendedInfosAsync().WaitAsync(TimeSpan.FromSeconds(2));
                        if (oldFrame != 0 && !infos.Any(i => i.AssociatedFrameInfos.Any(f => f.FrameId == oldFrame))) break;
                    }
                    catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or TimeoutException or InvalidOperationException or IOException)
                    {
                        if (oldProcess.HasExited) break;
                    }
                    await Task.Delay(500);
                }
                if (IsDisposed || ControllerRecoveryAddress != target) return;
                browser = new TableBrowser { Dock = DockStyle.Fill };
                layout.Controls.Add(browser, 0, 1);
                BrowserCreated?.Invoke(browser);
                await SetupBrowser();
                if (IsDisposed || ControllerRecoveryAddress != target) return;
                loadingRecovery = true;
                nextProbe = Now + 10000;
                browser.CoreWebView2.Navigate(target.AbsoluteUri);
                ShowStatus("旧出牌文档已关闭，已在原资料目录重建唯一控制器。");
                ReplacingBrowser = false;
            }
            catch (Exception ex)
            {
                // 不能证明旧帧已关闭时保持失权，避免创建并行出牌器。
                ShowStatus($"无法核实旧出牌文档已关闭或重建失败，原桌待核对（0x{ex.HResult:X8}）。");
            }
        };
        Shown += async (_, _) =>
        {
            try
            {
                ShowStatus("正在打开专用牌桌窗口…");
                await SetupBrowser();
                timer.Tick += async (_, _) => await TickAsync();
                timer.Start();
                loginButton.Enabled = true;
                if (!background && firstPage == null && vault.Preferred is { } preferred) BeginLogin(preferred);
                else Navigate(firstPage ?? new Uri(TableBrowser.Panel));
                RefreshHistory();
            }
            catch (Exception ex) { ShowStatus($"牌桌窗口启动失败，请检查本机的 Edge WebView2 运行时和资料目录（错误码 0x{ex.HResult:X8}）。"); }
        };
        FormClosed += (_, _) => timer.Dispose();
    }

    private void ShowStatus(string message, bool action = false)
    {
        // 仅接收助手生成的提示。账号、密码、页面地址和原始异常不得写入状态栏或日志。
        status.Text = message;
        if (operationLog.Record(message, action)) LogLine?.Invoke(message);
    }

    private void Disarm()
    {
        armed = ready = matchedOpponent = null;
        reconnect = loadingRecovery = platformPenaltyPage = false;
        ownDeadline = interfaceWaitSince = pendingRecoveryDeadline = null;
        lastClockAuditState = null;
        clockAuditFailed = false;
        journalPath = null;
        pending = null;
        recoveredIntent = null;
        projectionIntent = lastProjection = null;
        tableNavigation = null;
        armedAddress = null;
        unconfirmedPhase = null;
        toggle.Enabled = false;
        toggle.Text = "启动自动出牌";
    }

    private void Navigate(Uri uri)
    {
        if (browser.CoreWebView2 == null) { ShowStatus("牌桌窗口尚未就绪。"); return; }
        signingIn = null;
        Disarm();
        browser.CoreWebView2.Navigate(uri.AbsoluteUri);
    }

    public async Task SignInForSeriesAsync(LoginSite site)
    {
        var secret = vault.Read(site) ?? throw new InvalidOperationException("请先在自动登录中保存此站测试账号。");
        var attempt = new LoginAttempt(site, secret);
        Navigate(attempt.Entry);
        string message = "正在打开所选站点的登录页。";
        var deadline = DateTime.UtcNow.AddSeconds(65);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(400);
            if (browser.Source?.GetLeftPart(UriPartial.Authority) != attempt.Origin) continue;
            var result = await attempt.Tick(browser);
            if (result.Message != message) ShowStatus(result.Message);
            message = result.Message;
            if (!result.Done) continue;
            if (result.Success) return;
            throw new InvalidOperationException(message);
        }
        throw new TimeoutException(message);
    }

    private void BeginLogin(LoginSite site)
    {
        var secret = vault.Read(site);
        if (secret == null) { ShowStatus("请在「自动登录」中保存该站点的账号。"); return; }
        var attempt = new LoginAttempt(site, secret);
        Navigate(attempt.Entry);
        signingIn = attempt;
    }

    private async Task TickAsync()
    {
        if (ReplacingBrowser) return;
        if (fallback != null && !IsPlaying && (platformPenaltyPage || browser.Source?.AbsolutePath == "/penalty"))
        {
            FinishFallback("supervisor-fallback-rejected", "platform-penalty");
            return;
        }
        if (fallback is { PrearmTarget:not null, TriedReplacement:false } stuck && armed == null &&
            Now - stuck.PrearmStarted >= 1800)
        {
            // 原观察失效后，同一控件的原桌 JS 仍不返回；只复用验证旧帧退场后才重建的路径。
            stuck.TriedReplacement = true;
            await replaceHungBrowser();
            return;
        }
        bool matched = armed != null && matchedOpponent != null;
        if (matched && pending is { } checking && projectionIntent == checking.Id && Now >= projectionDeadline)
            ReportUnresolved(checking, "同步期限已到，最后观测 " + lastProjection);
        if (matched && platformPenaltyPage)
        {
            if (Now >= nextProbe) RecoverTable("处罚提醒后只读核对原桌，保留席位和待确认动作。");
            return;
        }
        if (matched && loadingRecovery)
        {
            if (Now >= nextProbe) RecoverTable("原桌仍未加载，继续只读核对。");
            return;
        }
        if (busy)
        {
            // 给已核实的短棋钟预留释放旧帧、重载和下一笔请求的时间。
            long limit = ownDeadline.HasValue ? Math.Clamp(ownDeadline.Value - tickStarted - 5000, 800, 28000) : 28000;
            if (matched && DrawHandoffMessage == null && Now - tickStarted >= limit)
            {
                // 后台渲染器可能不发 Unresponsive。保留短慢读，只在持续阻塞时关闭旧文档。
                await replaceHungBrowser();
            }
            return;
        }
        busy = true;
        int owner = ++tickOwner;
        tickStarted = Now;
        int revision = navigation;
        try
        {
            if (signingIn is { } attempt)
            {
                var result = await attempt.Tick(browser, () => !IsDisposed && attempt == signingIn && revision == navigation);
                if (IsDisposed || revision != navigation || attempt != signingIn) return;
                ShowStatus(result.Message);
                if (result.Done)
                {
                    signingIn = null;
                    loginNotice = result.Message;
                    if (result.Success && browser.Source?.AbsolutePath == "/account") Navigate(attempt.Destination);
                }
                return;
            }
            if (fallback != null && !await TickFallbackAsync(revision, owner)) return;
            matched = armed != null && matchedOpponent != null;
            // 接管后只读终局，不再生成出牌计划，也不因残留的己方按钮与接管人争抢同一手。
            bool handedOff = matched && DrawHandoffMessage != null;
            var read = handedOff ? await browser.ReadAsync() : matched ? await browser.ReadMatched(armed!, matchedOpponent!,
                recoverDraw: pending?.RegressedDrawAt != null || pending?.OriginalDraw != null) :
                await browser.ReadAsync(armed, unconfirmedPhase, matchedOpponent);
            if (IsDisposed || revision != navigation || owner != tickOwner) return;
            if (handedOff && read.Kind is not ("ended" or "result")) return;
            if (matched && armed != null && matchedOpponent != null)
            {
                bool knownClock = read.Table == armed && read.ClockStatus == "known" && read.ClockSeconds.HasValue;
                ownDeadline = knownClock ? Now + 1000L * Math.Max(0, read.ClockSeconds!.Value) : null;
                bool interfaceWait = read.Table == armed && read.WaitReason is "interface-lock" or "client-state";
                if (interfaceWait) interfaceWaitSince ??= Now;
                else interfaceWaitSince = null;
                long waited = interfaceWaitSince.HasValue ? Now - interfaceWaitSince.Value : 0;
                // 快速返回的锁/客户端等待不会触发 busy 看门狗。预算只用于重新读取原桌，绝不绕锁派发。
                long budget = knownClock ? Math.Clamp(1000L * read.ClockSeconds!.Value - 5000, 900, 5000) : 5000;
                // 本次提交前已核实的期限可收紧只读恢复预算；客户端态不会使它成为新的可出牌钟值。
                if (read.ClockStatus is "not-own-turn" or "not-realtime" or "server-transition") pendingRecoveryDeadline = null;
                if (interfaceWait && pendingRecoveryDeadline.HasValue)
                    budget = Math.Min(budget, Math.Clamp(pendingRecoveryDeadline.Value - interfaceWaitSince!.Value - 5000, 900, 5000));
                bool reread = interfaceWait && waited >= budget;
                AuditClock(read, waited, interfaceWait ? budget : null, reread ? "reread-original-table" : "observe");
                if (read.WaitReason == "draw-no-safe-discard" && pending is { RegressedDrawAt: { } observed } draw &&
                    draw.Plan.ActionKind == "draw-deck" && read.Table == draw.Table && read.ClockPhase == draw.Plan.Phase &&
                    read.PhaseSource == "last-server-state" &&
                    (!knownClock || read.ClockSeconds <= 45 || DateTimeOffset.UtcNow - observed >= TimeSpan.FromSeconds(3)))
                {
                    // 时间只决定何时让人接管。空弃牌堆名单和超时都不能证明牌库抽牌未受理。
                    pending = draw with { DrawHandoff = true };
                    DrawHandoffMessage = DrawHandoffNotice(draw.Table.Split('/')[1]);
                    ShowMatchedTable(DrawHandoffMessage);
                    try { SaveIntent(journalPath!, pending); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                        ShowStatus(DrawHandoffMessage + " 本机未能保存接管标记。");
                    }
                    return;
                }
                if (reread) {
                    RecoverTable("接口等待接近核对预算，只读原桌，保留待确认动作。");
                    return;
                }
                if (read.Kind == "offline")
                {
                    reconnect = true;
                    ShowStatus(read.Message);
                    return;
                }
                if (reconnect)
                {
                    reconnect = false;
                    RecoverTable("连接已恢复，先读取原桌的最新局面。");
                    return;
                }
                if (read.OpponentTurn != null && read.Table == armed) ObserveOpponentTurn(read);
                if (read.Kind == "plan" && read.Plan != null)
                {
                    read = await SubmitMatched(read, revision);
                    if (IsDisposed || revision != navigation || owner != tickOwner) return;
                }
                if (read.Kind == "resync")
                {
                    RecoverTable(read.Message);
                    return;
                }
            }
            if (read.Kind == "refresh-passive" && matchedOpponent != null && armed != null && read.Table == armed)
            {
                ShowStatus(read.Message);
                browser.CoreWebView2.Reload();
                return;
            }
            if (read.Kind == "resync" && read.Phase != null && armed != null && read.Table == armed)
            {
                unconfirmedPhase = read.Phase;
                ShowStatus(read.Message);
                browser.CoreWebView2.Reload();
                return;
            }
            if (read.Kind == "action" && read.Phase != null) unconfirmedPhase = null;
            if (read.Kind == "action" && matchedOpponent != null) MatchedActions++;
            if (read.Kind == "result" && read.Result != null)
            {
                Disarm();
                if (!batchVerifiesResults && recorded != read.Table)
                {
                    StoreResult(read.Result);
                    recorded = read.Table;
                }
                return;
            }
            if (read.Kind == "ended" && read.Table != null && browser.Source is { } current)
            {
                ShowStatus("本局已结束，正在打开官方结算页。");
                var resultPage = new Uri(current, "/table?table=" + read.Table.Split('/')[1]);
                if (matched)
                {
                    // 旧 game 观察在 NavigationStarting 才失效。由同一事件撤销旧读后释放席位。
                    if (armed?.StartsWith(read.Table + "/座位/", StringComparison.Ordinal) == true)
                        browser.CoreWebView2.Navigate(resultPage.AbsoluteUri);
                }
                else Navigate(resultPage);
                return;
            }
            if (read.Kind == "ready") { if (armed != read.Table) Disarm(); ready = read.Table; }
            else if (read.Kind is "unsupported" or "stalled") { Disarm(); ready = read.Table; pauseReason = read.Message; }
            else if (read.Kind is "idle" or "tutorialDone" || (armed != null && read.Table != null && read.Table != armed)) Disarm();
            toggle.Enabled = armed != null || ready != null;
            toggle.Text = armed == null ? "启动自动出牌" : "暂停自动出牌";
            ShowStatus(loginNotice != null && read.Kind == "idle" ? loginNotice :
                read.Kind == "ready" && pauseReason != null ? pauseReason :
                (armed != null ? "自动 · " : "") + read.Message, action: read.Kind == "action");
        }
        catch (Exception ex)
        {
            if (!IsDisposed && revision == navigation)
            {
                if (fallback != null && !matched)
                {
                    if (ex is InvalidDataException or InvalidOperationException)
                        FinishFallback("supervisor-fallback-rejected", "invalid-state-or-intent");
                    else if (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or System.Text.Json.JsonException)
                        ShowStatus("原桌救场暂时无法读取，保留授权和原意图，只核对同一桌。");
                    else FinishFallback("supervisor-fallback-rejected", "unexpected-controller-failure");
                    return;
                }
                if (matched && armed != null && matchedOpponent != null &&
                    ex is System.Runtime.InteropServices.COMException or InvalidOperationException or System.Text.Json.JsonException)
                {
                    RecoverTable("无法读取原桌文档，保留席位和待确认动作。");
                    return;
                }
                signingIn = null;
                Disarm();
                pauseReason = ex is IOException or UnauthorizedAccessException
                    ? "助手已暂停，本机战绩或配置文件读写失败。"
                    : "助手已暂停，无法确认页面状态，请检查牌桌后重新启动。";
                ShowStatus(pauseReason);
            }
        }
        finally { if (owner == tickOwner) busy = false; }
    }

    private void AuditClock(Reading read, long waited, long? budget, string decision)
    {
        if (journalPath == null || read.Table != armed) return;
        string clockBand = read.ClockStatus == "known" ? (read.ClockSeconds <= 10 ? "urgent" : "normal") : "unknown";
        string state = $"{read.ClockStatus}/{clockBand}/{read.ClockPhase}/{read.PhaseSource}/{read.WaitReason}/{decision}/{navigation}";
        // 数值倒计时最多每两秒采样；身份、阶段、等待种类和恢复决策变化立即记录。
        if (state == lastClockAuditState && Now - lastClockAudit < 2000) return;
        lastClockAudit = Now;
        lastClockAuditState = state;
        try {
            Directory.CreateDirectory(seatFolder);
            var observation = new { at = DateTimeOffset.UtcNow, read.ClockReadAt, table = armed, opponent = matchedOpponent,
                generation = navigation, read.ClockStatus, read.ClockSeconds, read.ClockSource, read.ClockPhase,
                read.PhaseSource, read.WaitReason, waitMilliseconds = waited, budgetMilliseconds = budget, decision,
                pendingIntent = pending?.Id, verifiedDeadlineRemainingMs = pendingRecoveryDeadline - Now };
            File.AppendAllText(Path.ChangeExtension(journalPath, ".clock.jsonl"),
                System.Text.Json.JsonSerializer.Serialize(observation) + Environment.NewLine);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            if (!clockAuditFailed) ShowStatus("棋钟观测记录写入失败，保留当前席位与待确认动作。");
            clockAuditFailed = true;
        }
    }

    private void RecoverTable(string reason)
    {
        if (armedAddress == null || matchedOpponent == null || DrawHandoffMessage != null || IsDisposed) return;
        ++navigation;
        ++tickOwner;
        busy = false;
        loadingRecovery = true;
        nextProbe = Now + 10000;
        revoked.TrySetResult();
        revoked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ShowStatus(reason);
        browser.CoreWebView2.Navigate(armedAddress.AbsoluteUri);
    }

    private static string DrawHandoffNotice(string number) =>
        $"需要立即接管原桌 {number}：抽牌是否受理无法核实，当前没有可核实的合法明牌。自动出牌和续排已停止。";

    private void ReportUnresolved(Intent prior, string observation, bool sameScoreConfirmation = false)
    {
        if (reportedUnresolvedIntent == prior.Id) return;
        reportedUnresolvedIntent = prior.Id;
        UnresolvedScoreIntent = sameScoreConfirmation ? prior.Id : null;
        MatchedReceiptUnresolved?.Invoke(prior.Table, prior.Id);
        try { ShowStatus("重新核对原桌后仍无法确认上一笔，暂停新匹配，保留当前席位。" + observation); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            Console.Error.WriteLine("未能写入原桌回执诊断。");
        }
    }

    private void ObserveOpponentTurn(Reading reading)
    {
        if (pending is not { } prior || reading.OpponentTurn is not { } turn || prior.Generation != navigation ||
            prior.Plan.DocumentId != turn.DocumentId || prior.Plan.ActionKind != "end-turn" || !prior.Plan.OfficialOwn || prior.Plan.Local.Length != 0 ||
            prior.PhaseSource != "last-server-state" || reading.PhaseSource != prior.PhaseSource ||
            prior.Plan.Phase != "40/playCards/" + prior.Table[(prior.Table.LastIndexOf('/') + 1)..] ||
            prior.Plan.Move is not long oldMove || turn.Move <= (prior.OpponentMove ?? oldMove)) return;
        // 保留已经交接的原意图及最高可见行动号，防止重载时陈旧的己方页面重复出牌。
        var observed = prior with { OpponentMove = turn.Move, FirstHandoff = prior.FirstHandoff ?? turn };
        File.WriteAllText(journalPath + ".tmp", System.Text.Json.JsonSerializer.Serialize(observed, MatchBook.Json));
        File.Move(journalPath + ".tmp", journalPath!, true);
        pending = observed;
        if (prior.OpponentMove == null)
            RecordProgress(prior, turn.Phase, turn.Move, null, false, reading.PhaseSource, "opponent-turn");
    }

    private void RecordProgress(Intent prior, string phase, long? move, bool? boardChanged, bool newDocument,
        string? phaseSource, string evidence, bool count = true)
    {
        var proof = new { at = DateTimeOffset.UtcNow, prior.Id, prior.Table, prior.Opponent, prior.Generation,
            priorPhase = prior.Plan.Phase, phase, priorMove = prior.Plan.Move, move, priorTarget = prior.Plan.Target,
            boardChanged, newDocument, priorPhaseSource = prior.PhaseSource, phaseSource, evidence };
        string ledger = Path.ChangeExtension(journalPath!, ".progress.jsonl");
        string line = System.Text.Json.JsonSerializer.Serialize(proof) + Environment.NewLine;
        if (evidence == "opponent-turn") {
            // 此条跨进程补证需要可重复核对。替换后只可能看见完整旧账本或完整新账本。
            if (File.Exists(ledger)) File.Copy(ledger, ledger + ".tmp", true);
            else File.WriteAllText(ledger + ".tmp", "");
            File.AppendAllText(ledger + ".tmp", line);
            File.Move(ledger + ".tmp", ledger, true);
        } else File.AppendAllText(ledger, line);
        if (count) { lastConfirmedIntent = prior.Id; MatchedConfirmedActions++; }
    }

    private async Task<Reading> SubmitMatched(Reading reading, int revision)
    {
        string table = armed!, opponent = matchedOpponent!, path = journalPath!;
        SeatPlan plan = reading.Plan!;
        Intent? replacedDraw = null;
        if (pending is { } prior)
        {
            var old = prior.Plan;
            bool fresh = old.DocumentId != plan.DocumentId;
            bool moveAdvanced = old.Move.HasValue && plan.Move > old.Move;
            bool regressed = old.Move.HasValue && plan.Move < old.Move;
            bool advanced = !regressed && (old.Phase != plan.Phase || (moveAdvanced && plan.Board != old.Board));
            string self = table[(table.LastIndexOf('/') + 1)..];
            bool nextOwnPhase = old.ActionKind switch {
                "end-turn" => old.Phase == $"40/playCards/{self}" &&
                    (plan.Phase == $"20/takeCards/{self}" || plan.ActionKind == "round-ack"),
                "draw-deck" => old.Phase == $"20/takeCards/{self}" && plan.Phase == $"30/chooseCard/{self}",
                "place-discard" => old.Phase == $"35/putDiscardPile/{self}" && plan.Phase == $"40/playCards/{self}",
                _ => false
            };
            bool strictNext = fresh && nextOwnPhase && old.OfficialOwn && prior.PhaseSource == "last-server-state";
            long? floor = prior.OpponentMove ?? old.Move;
            bool verifiedOwn = plan.OfficialOwn && reading.PhaseSource == "last-server-state";
            bool sameDraw = old.ActionKind == "draw-deck" && fresh && verifiedOwn && old.OfficialOwn &&
                prior.PhaseSource == "last-server-state" && prior.OpponentMove == null &&
                old.Phase == $"20/takeCards/{self}" && plan.Phase == old.Phase && old.Board == plan.Board &&
                old.Move.HasValue && plan.Move == old.Move - 1;
            if (sameDraw && prior.RegressedDrawAt == null)
            {
                prior = prior with { RegressedDrawAt = DateTimeOffset.UtcNow };
                SaveIntent(path, prior);
                pending = prior;
            }
            string Describe() {
                string phase = plan.Phase.Length <= 96 && plan.Phase.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '_')
                    ? plan.Phase : "未知";
                return $"意图 {prior.Id}，原行动号 {old.Move?.ToString() ?? "未知"}，对手最高 {prior.OpponentMove?.ToString() ?? "未知"}，当前 {plan.Move?.ToString() ?? "未知"}，阶段 {phase}，新文档 {fresh}，盘面变化 {old.Board != plan.Board}。";
            }
            // 新进程没有旧页的点击回执。第一笔只能由新的正式己方阶段、行动号、盘面和本席棋钟共同解锁。
            // 相同结束回合、未知时钟或相同多人确认都不能借恢复入口再发。
            bool afterRecoveredMove = plan.Move > old.Move && (prior.OpponentMove == null || plan.Move > prior.OpponentMove ||
                prior.FirstHandoff != null && nextOwnPhase && plan.Move == prior.OpponentMove);
            if (recoveredIntent == prior.Id && !(fresh && verifiedOwn && reading.ClockStatus == "known" &&
                reading.ClockSeconds is >= 0 && reading.ClockSource == "timeToThink_" + self &&
                afterRecoveredMove && old.Phase != plan.Phase && old.Board != plan.Board &&
                !(old.ActionKind is "end-turn" or "round-ack" && old.ActionKind == plan.ActionKind))) {
                ReportUnresolved(prior, "重启后仍缺少原意图的受理证据。" + Describe());
                if (Now - pendingSince >= 3500 && Now >= nextProbe) {
                    nextProbe = Now + 3500;
                    RecoverTable("重启后只读核对原桌；保留原意图、停止续排。");
                }
                return new("wait", "恢复需接管：原意图未明，原桌继续只读核对。", table);
            }
            // 行动号是上一手编号。对手最后一手与下一位的可行动页面可以同号。
            bool ownAfterHandoff = prior.OpponentMove.HasValue && verifiedOwn && plan.Move > old.Move &&
                (plan.Move > prior.OpponentMove || nextOwnPhase && plan.Move == prior.OpponentMove);
            if (prior.OpponentMove.HasValue) advanced = ownAfterHandoff;
            else if (old.OfficialOwn && old.ActionKind != null && !verifiedOwn) advanced = false;
            var originalDraw = prior.OriginalDraw ?? prior;
            if (originalDraw.RegressedDrawAt is { } observed)
            {
                // 退一号的原桌已经实际读过。选牌是抽牌已受理的证据；同一取牌画面本身不能证明提交结果。
                bool canKeep = verifiedOwn && plan.ActionKind == "keep-drawn" && plan.Phase == $"30/chooseCard/{self}" &&
                    plan.Move >= originalDraw.Plan.Move && plan.Board != originalDraw.Plan.Board;
                if (canKeep)
                {
                    advanced = true;
                    strictNext = false;
                    prior = originalDraw; // 后到的选牌证明原抽牌，不能把它归到备用明牌。
                }
                else if (prior.OriginalDraw != null)
                {
                    // 备用动作在点击前已落盘。页面重读或恢复进程时，必须看到那张明牌已入手才可继续。
                    advanced = verifiedOwn && plan.Phase == $"40/playCards/{self}" && moveAdvanced &&
                        old.RecoveryCard != null && System.Text.Json.JsonSerializer.Deserialize<string[][]>(plan.Board)
                            is { Length: > 0 } zones && Array.IndexOf(zones[0], old.RecoveryCard) >= 0;
                }
                else
                {
                    // 给同一页面短暂的同步时间；低钟时优先执行当前服务器允许的不同动作。
                    bool ready = DateTimeOffset.UtcNow - observed >= TimeSpan.FromSeconds(2) || reading.ClockSeconds is >= 0 and <= 5;
                    advanced = sameDraw && ready && plan.ActionKind == "recover-discard" && !string.IsNullOrEmpty(plan.RecoveryCard);
                    if (advanced) replacedDraw = prior;
                }
            }
            if (plan.ActionKind == "round-ack")
            {
                // 同一次多人确认中，对手行动也会改变行动号和盘面。已派发的确认不能因此再发。
                if (old.ActionKind == "round-ack") advanced = false;
                else if (old.ActionKind == "end-turn")
                    advanced = nextOwnPhase && old.OfficialOwn && prior.PhaseSource == "last-server-state" &&
                        verifiedOwn && moveAdvanced && (!prior.OpponentMove.HasValue || ownAfterHandoff);
            }
            // 两个本人正式阶段与盘面均已变化，行动号却落后，只能说明这次读不自洽。
            // 只延后判定；上一手即使是多人确认，也须等新页同步。同一次确认仍禁止再次提交。
            bool changedOwnState = fresh && old.OfficialOwn && prior.PhaseSource == "last-server-state" && verifiedOwn &&
                old.Phase != plan.Phase && old.Board != plan.Board && old.Local.Length == 0 &&
                !(old.ActionKind == "round-ack" && plan.ActionKind == "round-ack") &&
                prior.OpponentMove == null && prior.RegressedDrawAt == null && prior.OriginalDraw == null;
            if (projectionIntent == prior.Id && checkingPhaseChange && !changedOwnState) {
                ReportUnresolved(prior, Describe());
                return new("wait", "原桌行动方、阶段或盘面无法核实，保留原意图。", table);
            }
            // 未越过原行动号时不确认、不派发。所有重读共用该意图最早的截止点。
            if ((strictNext || changedOwnState) && floor.HasValue &&
                !(verifiedOwn && (prior.OpponentMove.HasValue ? ownAfterHandoff : plan.Move > floor))) {
                lastProjection = Describe();
                if (projectionIntent != prior.Id) {
                    projectionIntent = prior.Id;
                    checkingPhaseChange = !strictNext;
                    projectionDeadline = Now + 2500;
                    ShowStatus("原桌官方阶段已切换，等候行动号同步。" + lastProjection);
                } else if (Now >= projectionDeadline) ReportUnresolved(prior, lastProjection);
                return new("wait", "正在只读核对新页的行动号，保留原意图。", table);
            }
            // 选中己方手牌是本地 UI 步骤。只有选中态已变化或新文档重新选牌时才继续。
            bool localSelection = old.Local.Length > 0 && (fresh || old.Target != plan.Target);
            if (!advanced && !localSelection)
            {
                if (fresh && reportedUnresolvedIntent != prior.Id)
                    ReportUnresolved(prior, Describe(), old.ActionKind == "round-ack" && plan.ActionKind == "round-ack" &&
                        old.OfficialOwn && verifiedOwn && old.Phase == plan.Phase && plan.Move >= old.Move);
                if (Now - pendingSince >= 3500 && Now >= nextProbe)
                {
                    nextProbe = Now + 3500;
                    RecoverTable("原桌上一步回执待核对，只读取最新局面。");
                }
                return new("wait", "回执未确认，保留原桌，等候可核实的服务端进展。", table);
            }
            projectionIntent = lastProjection = null;
            if (advanced && replacedDraw == null && old.Local.Length == 0 && prior.OpponentMove == null && prior.Id != lastConfirmedIntent)
                RecordProgress(prior, plan.Phase, plan.Move, prior.Plan.Board != plan.Board, prior.Plan.DocumentId != plan.DocumentId,
                    reading.PhaseSource, "next-plan");
        }
        var previous = pending;
        var previousDeadline = pendingRecoveryDeadline;
        var intended = new Intent(Guid.NewGuid().ToString("N"), table, opponent, revision, plan, reading.PhaseSource, OriginalDraw: replacedDraw);
        Directory.CreateDirectory(seatFolder);
        // 点击之前原子落盘。即使 ExecuteScriptAsync 的返回丢失，下一代仍能核对此意图。
        SaveIntent(path, intended);
        pending = intended;
        pendingRecoveryDeadline = ownDeadline;
        pendingSince = Now;
        var sent = await browser.ReadMatched(table, opponent, plan, recoverDraw: plan.ActionKind is "recover-discard" or "keep-drawn");
        if (IsDisposed || revision != navigation || intended != pending) return sent;
        // 只有适配器明确在 el.click() 前返回才撤回意图。空回执、脚本异常都保留意图。
        if (sent.NotSubmitted)
        {
            if (previous == null) File.Delete(path);
            else
            {
                File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(previous, MatchBook.Json));
                File.Move(path + ".tmp", path, true);
            }
            pending = previous;
            pendingRecoveryDeadline = previousDeadline;
            if (sent.Kind == "offline") reconnect = true;
        }
        return sent;
    }

    private static void SaveIntent(string path, Intent intent)
    {
        File.WriteAllText(path + ".tmp", System.Text.Json.JsonSerializer.Serialize(intent, MatchBook.Json));
        File.Move(path + ".tmp", path, true);
    }

    private void RefreshHistory()
    {
        try { matches = book.Load(); }
        catch (Exception) { totals.Text = "读取战绩失败，请检查本机战绩文件。"; return; }
        var previous = players.SelectedItem as PlayerChoice;
        var all = new PlayerChoice("", "", "全部已收录牌桌");
        var list = matches.SelectMany(x => x.Match.Players.Select(p =>
            new PlayerChoice(x.Match.Site, p.Id, $"{p.Name} · {x.Match.Site} #{p.Id}")))
            .DistinctBy(p => (p.Site, p.Id)).OrderBy(p => p.Title).Prepend(all).ToList();
        players.DataSource = list;
        players.SelectedItem = list.FirstOrDefault(p => p.Site == previous?.Site && p.Id == previous.Id) ?? all;
        RenderMatches();
    }

    private void RenderMatches()
    {
        grid.Rows.Clear();
        var player = players.SelectedItem as PlayerChoice;
        if (player == null) return;
        foreach (var item in matches)
        {
            var g = item.Match;
            if (player.Id != "" && (g.Site != player.Site || !g.Players.Any(x => x.Id == player.Id))) continue;
            var ranks = string.Join("　", g.Players.OrderBy(p => p.Rank).Select(p => $"#{p.Rank} {p.Name} {p.Score}分"));
            grid.Rows.Add(item.Recorded.LocalDateTime.ToString("yyyy-MM-dd HH:mm"), g.Site, g.Table, ranks);
        }
        var stats = MatchBook.ForPlayer(matches.Select(x => x.Match), player.Site, player.Id);
        totals.Text = player.Id == "" ? $"已收录 {matches.Count} 桌 · 选择玩家查看胜率" :
            $"{stats.Played} 局　独胜 {stats.Wins}　并列第一 {stats.Ties}　独胜率 {(double)stats.Wins / Math.Max(1, stats.Played):P1}　均分 {stats.Average:F1}";
    }
}
