using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal sealed class AccountNotice : Form
{
    private const string Origin = "https://boardgamearena.com";
    private const string Project = @"D:\code\codexwork\projects\bga-seasaltpaper";
    private const string ReaderExe = "51A6FE5F73056F48450132EBE94F1CE6AE056D25E988D43445B8B3441F551111";
    private const string ReaderDll = "F880FD01290E007FF298DD9A294E8A5B8D7E223658111D943780A1F88C0784C0";
    private static readonly string OfficialEvidence = Path.Combine(Project,"evidence");
    private static readonly string Watch = Path.Combine(OfficialEvidence,"2026-11-05-hundred","批次监管");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WebView2 web = new() {Dock=DockStyle.Fill};
    private readonly string script = new StreamReader(typeof(AccountNotice).Assembly.GetManifestResourceStream("notice.js")!).ReadToEnd();
    private readonly string dialogScript = new StreamReader(typeof(AccountNotice).Assembly.GetManifestResourceStream("dialogs.js")!).ReadToEnd();
    private readonly string snapshotScript = new StreamReader(typeof(AccountNotice).Assembly.GetManifestResourceStream("lobby_snapshot.js")!).ReadToEnd();
    private readonly HashSet<string> blockedSamples=[];
    private readonly string mode, evidence, log, profile;
    private readonly string? fixture, requiredTable, requiredTime, expectedObservation, batchFolder;
    private int exit = 1;
    private string? tableRead, heldNavigation;
    private DateTimeOffset? serverDate;
    private long serverTick;
    private bool marked;
    private int documents, blocked, attempted;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p=base.CreateParams; p.ExStyle|=0x08000000|0x80; return p; } }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle,out uint pid);

    private AccountNotice(string mode,string evidence,string profile,string? fixture,string? table,string? when,string? batchLog=null,string? expected=null)
    {
        this.mode=mode; this.evidence=evidence; this.profile=profile; this.fixture=fixture;
        requiredTable=table; requiredTime=when; expectedObservation=expected;
        batchFolder=batchLog==null?null:Path.GetDirectoryName(batchLog);
        string logs=batchLog==null?Path.Combine(evidence,"2026-11-05-hundred","批次监管","账号提醒","运行记录"):Path.GetDirectoryName(batchLog)!;
        Directory.CreateDirectory(logs);
        log=batchLog??Path.Combine(logs,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N")+".jsonl");
        using(var claim=new FileStream(log,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){}
        Location=new(-32000,-32000);StartPosition=FormStartPosition.Manual;Size=new(1200,900);
        ShowInTaskbar=false;Opacity=0;Controls.Add(web);Shown+=async (_,_)=>await Run();
    }
    private void Record(string stage,object data)
    {
        var line=JsonSerializer.Serialize(new {at=DateTimeOffset.UtcNow,stage,data},Json);
        File.AppendAllText(log,line+Environment.NewLine); Console.WriteLine(line);
    }
    private static string? Get(JsonElement p,string key)=>p.ValueKind==JsonValueKind.Object&&p.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static bool Yes(JsonElement p,string key)=>p.ValueKind==JsonValueKind.Object&&p.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.True;
    private static object EvidencePage(JsonElement p)
    {
        var data=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(p.GetRawText())!;
        if(data.Remove("key",out var full))data["fingerprint"]=JsonSerializer.SerializeToElement(DigestKey(full.GetString()!));
        data["observationId"]=JsonSerializer.SerializeToElement(Observation(p));
        return data;
    }
    private static string? Observation(JsonElement p)
    {
        string? kind=Get(p,"kind");
        if(Get(p,"key") is string key)return DigestKey(key);
        if(kind=="ordinary")return DigestKey(string.Join("|",kind,Get(p,"hostId"),Get(p,"table"),Get(p,"when"),Get(p,"penalty")));
        if(kind=="cooldown"&&Get(p,"title")!=null&&(Get(p,"untilUtc")!=null||p.TryGetProperty("remainingSeconds",out _)))
            return DigestKey(string.Join("|",kind,Get(p,"hostId"),Get(p,"path"),Get(p,"title"),Get(p,"untilUtc")??"relative-no-control"));
        return null;
    }
    private static void IdleProcesses()
    {
        foreach(string name in new[]{"SeatControl","SeaSaltDesk","ReadOnly","NoticeAck"})
            foreach(var p in Process.GetProcessesByName(name)) using(p) {
                if(p.Id!=Environment.ProcessId) throw new InvalidOperationException("正式助手或账号采集仍在运行");
            }
        string series=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SeaSaltDesk","series");
        if(Directory.Exists(series))
            foreach(string file in Directory.EnumerateFiles(series,"*.run.lock"))
                using(new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)) { }
        foreach(var dir in Directory.EnumerateDirectories(Path.Combine(Watch,"批次")))
            if(File.Exists(Path.Combine(dir,"发车意图.json"))&&!File.Exists(Path.Combine(dir,"退出.json")))
                throw new InvalidOperationException("监管仍有未收尾批次");
    }
    private async Task<JsonElement> Page(string op,JsonElement? previous=null)
    {
        var js=script+"("+JsonSerializer.Serialize(op)+","+JsonSerializer.Serialize(previous)+","+dialogScript+")";
        using var doc=JsonDocument.Parse(await web.CoreWebView2.ExecuteScriptAsync(js).WaitAsync(TimeSpan.FromSeconds(6)));
        return doc.RootElement.Clone();
    }
    // SPA 改地址或只替换内容时可能不触发 NavigationCompleted，因此按实际 DOM 复核。
    private async Task<JsonElement> Observe(Func<JsonElement,bool> done,string op="inspect",JsonElement? previous=null,int seconds=18)
    {
        var timer=Stopwatch.StartNew(); JsonElement last=default;
        while(timer.Elapsed.TotalSeconds<seconds) {
            await Task.Delay(200);
            try { var p=await Page(op,previous); if(p.ValueKind==JsonValueKind.Object){last=p;if(done(p))break;} }
            catch(COMException) { }
        }
        if(last.ValueKind==JsonValueKind.Undefined)throw new InvalidDataException("页面未完成只读观察");
        return last;
    }
    private string Marker(string id)=>Path.Combine(evidence,"2026-11-05-hundred","批次监管","处置复核",id+"-confirm-once.json");
    private bool Marked(string id)=>Directory.EnumerateFiles(evidence,id+"-confirm-once*",SearchOption.AllDirectories)
        .Any(f=>Path.GetFileName(f)==id+"-confirm-once"||Path.GetFileName(f)==id+"-confirm-once.json");
    private void Claim(JsonElement proof)
    {
        string id=Get(proof,"table")!;
        if(Marked(id))throw new IOException("历史标记已存在");
        string marker=Marker(id);Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        using var f=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None);
        JsonSerializer.Serialize(f,new {at=DateTimeOffset.UtcNow,noticeTable=id,when=Get(proof,"when"),output=Path.GetFileName(log),
            hostId="99769903",pid=Environment.ProcessId,proof},Json); f.Flush(true);
        marked=true;
    }
    private static bool Official(Uri u)=>u.Scheme=="https"&&u.Host=="boardgamearena.com"&&u.IsDefaultPort&&u.UserInfo=="";
    private static bool SecurityPath(string path)=>System.Text.RegularExpressions.Regex.IsMatch(path,@"^/(?:account/)?(?:security|verify|verification|challenge|twofactor)(?:/(?:start|continue|verify|check|device|code))?/?$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private bool AllowedDocument(Uri u)=>Official(u)&&(u.AbsolutePath is "/lobby" or "/penalty" || SecurityPath(u.AbsolutePath) ||
        marked && u.AbsolutePath is ("/welcome" or "/"));
    private bool ReadAllowed(CoreWebView2WebResourceRequestedEventArgs e,Uri u)
    {
        if(e.Request.Method is not ("GET" or "HEAD"))return false;
        if(mode is "strong" or "fixed") {
            // 只读进程没有操作入口。沿用 E 已实测能完成水合的 GET/HEAD 策略，同时拒绝对局与已知匹配写端点。
            if(u.Scheme!="https"||u.UserInfo!="")return false;
            if(e.ResourceContext==CoreWebView2WebResourceContext.Document)
                return Official(u)&&u.AbsolutePath is ("/lobby" or "/penalty");
            string path=u.AbsolutePath;
            return path is not ("/table" or "/tableview") && !path.StartsWith("/table/",StringComparison.OrdinalIgnoreCase) &&
                !path.EndsWith("/seasaltpaper",StringComparison.OrdinalIgnoreCase) &&
                !System.Text.RegularExpressions.Regex.IsMatch(path,@"/(?:wannaplay|cancel(?:Realtime)?Matchmaking|extendMatchmakingInProgress|setGamePreference|join|resign|startGame)\.html$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        if(e.ResourceContext==CoreWebView2WebResourceContext.Document)return AllowedDocument(u);
        if(Official(u)&&u.AbsolutePath=="/table/table/tableinfos.html") {
            var q=System.Web.HttpUtility.ParseQueryString(u.Query);
            return tableRead!=null && q.GetValues("id") is [var id] && id==tableRead;
        }
        bool cdn=u.Scheme=="https"&&u.IsDefaultPort&&u.UserInfo==""&&
            (u.Host=="boardgamearena.com"||u.Host.EndsWith(".boardgamearena.com",StringComparison.Ordinal)||
             u.Host=="boardgamearena.net"||u.Host.EndsWith(".boardgamearena.net",StringComparison.Ordinal));
        return cdn && u.AbsolutePath.Contains('.') && !u.AbsolutePath.EndsWith(".html",StringComparison.OrdinalIgnoreCase) &&
            System.Text.RegularExpressions.Regex.IsMatch(u.AbsolutePath,@"\.(js|css|png|jpe?g|webp|svg|ico|woff2?|ttf|gif|map)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    private string RecoveryFolder=>Path.Combine(evidence,"2026-11-05-hundred","批次监管","账号提醒","处置记录");
    private string CooldownFile=>Path.Combine(RecoveryFolder,"当前冷却.json");
    private static string DigestKey(string value)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));
    private void RecordFreshClear(JsonElement page)
    {
        if(Get(page,"kind")!="idle"||!Yes(page,"readySeaSalt")||!Directory.Exists(RecoveryFolder))return;
        // 普通弹窗按连续出现的一次通知防重。另一次新大厅已实际加载且弹窗消失后，保留旧记录，允许日后新通知。
        // 普通未完赛的历史桌 marker 永远保留，位于另一个目录。
        foreach(var old in Directory.EnumerateFiles(RecoveryFolder,"*-once.json")){
            string closed=old+".cleared.json";if(File.Exists(closed))continue;
            Publish(closed,new {at=DateTimeOffset.UtcNow,pid=Environment.ProcessId,proof="fresh-ready-lobby",log=Path.GetFileName(log)});
        }
    }
    private static void Publish(string path,object value)
    {
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){JsonSerializer.Serialize(stream,value,Json);stream.Flush(true);}
        File.Move(temp,path,true);
    }
    private DateTimeOffset? SaveDeadline(JsonElement p)
    {
        if(serverDate is not { } httpDate)return null;
        var observed=httpDate.AddMilliseconds(Environment.TickCount64-serverTick);
        DateTimeOffset? absolute=DateTimeOffset.TryParse(Get(p,"untilUtc"),out var t)?t.ToUniversalTime():null;
        long? seconds=p.TryGetProperty("remainingSeconds",out var x)&&x.TryGetInt64(out long n)&&n>=0?n:null;
        if(absolute==null&&seconds==null)return null;
        // 若平台同时显示两种期限，按两者较晚者等待；HTTP Date 精确到秒。
        var until=absolute??observed.AddSeconds(seconds!.Value);
        if(seconds.HasValue&&observed.AddSeconds(seconds.Value)>until)until=observed.AddSeconds(seconds.Value);
        var localDue=DateTimeOffset.UtcNow.Add(until>observed?until-observed:TimeSpan.Zero).AddSeconds(1);
        Directory.CreateDirectory(RecoveryFolder);
        var entry=new {at=DateTimeOffset.UtcNow,source="official-page",httpDate,observedServerUtc=observed,absolute,remainingSeconds=seconds,
            untilUtc=until,localDue,serverWording=Get(p,"serverWording"),released=false};
        Publish(CooldownFile,entry);Record("cooldown-deadline",entry);return localDue;
    }
    private async Task<int> WaitCooldown(DateTimeOffset when)
    {
        Record("cooldown-wait",new {until=when,noQueue=true});
        while(true){var delay=when-DateTimeOffset.UtcNow;if(delay<=TimeSpan.Zero)break;await Task.Delay(delay>TimeSpan.FromMinutes(1)?TimeSpan.FromMinutes(1):delay);}
        if(fixture==null)IdleProcesses();
        var core=web.CoreWebView2;var finished=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Completed(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>finished.TrySetResult(e.IsSuccess&&e.HttpStatusCode is >=200 and <300);
        core.NavigationCompleted+=Completed;
        try {core.Navigate(Origin+"/lobby");if(!await finished.Task.WaitAsync(TimeSpan.FromSeconds(12)))return 6;}
        finally {core.NavigationCompleted-=Completed;}
        var timer=Stopwatch.StartNew();
        var fresh=await Observe(p=>Get(p,"kind") is not (null or "loading")&&(Get(p,"kind")!="idle"||
            Yes(p,"readySeaSalt")&&timer.ElapsedMilliseconds>=2200));
        bool released=Get(fresh,"kind")=="idle"&&Yes(fresh,"readySeaSalt")&&Get(fresh,"path")=="/lobby"&&heldNavigation==null;
        Record("cooldown-recheck",new {released,page=EvidencePage(fresh),noQueue=true});
        if(released){
            using var prior=JsonDocument.Parse(File.ReadAllBytes(CooldownFile));var row=prior.RootElement.Clone();
            Publish(CooldownFile,new {released=true,recheckedAt=DateTimeOffset.UtcNow,prior=row,condition="新的原站大厅已载入、无可见冷却或在途桌"});
        }else if(Get(fresh,"kind")=="cooldown")SaveDeadline(fresh);
        return released?0:Get(fresh,"kind")=="cooldown"?9:5;
    }
    private async Task<int> HandleDialog(JsonElement before)
    {
        string kind=Get(before,"kind")!;
        if(mode=="inspect")return Get(before,"need")!=null?8:Yes(before,"eligible")?0:5;
        if(Get(before,"need")!=null){Record("personal-step",new {need=Get(before,"need")});return 8;}
        DateTimeOffset? due=kind=="cooldown"?SaveDeadline(before):null;
        if(kind=="cooldown"&&!due.HasValue){Record("decision",new {reason="缺少官方时间依据，稍后重新只读检查"});return 5;}
        var current=await Page("inspect");
        if(Get(current,"kind")!=kind||Get(current,"key")!=Get(before,"key"))return 5;
        if(Yes(current,"eligible")){
            if(fixture==null)IdleProcesses();
            string key=DigestKey(Get(current,"key")!);
            Directory.CreateDirectory(RecoveryFolder);
            if(Directory.EnumerateFiles(RecoveryFolder,key+"-*-once.json").Any(p=>!File.Exists(p+".cleared.json"))){
                Record("decision",new {reason="already-marked",kind,key});
                return due.HasValue?await WaitCooldown(due.Value):4;
            }
            string marker=Path.Combine(RecoveryFolder,key+"-"+Guid.NewGuid().ToString("N")+"-once.json");
            using(var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.None)){
                JsonSerializer.Serialize(stream,new {at=DateTimeOffset.UtcNow,key,kind,title=Get(current,"title"),log=Path.GetFileName(log)},Json);stream.Flush(true);
            }
            marked=true;JsonElement action=default;
            try {action=await Page("click",current);}
            catch(Exception e) when(e is COMException or TimeoutException or JsonException){Record("click-return",new {kind=e.GetType().Name});}
            Record("action",new {key,kind,text=Get(current,"actionLabel"),target=Get(current,"targetPath"),dispatched=Yes(action,"dispatched"),
                reportedCount=Yes(action,"dispatched")?1:0,markerReserved=true});
            var after=await Observe(p=>Get(p,"kind")!=kind||Get(p,"need")!=null||Get(p,"key")!=Get(current,"key"),seconds:3);
            Record("after",EvidencePage(after));
            if(!Yes(action,"dispatched"))return 6;
            if(Get(after,"need") is { } need){Record("personal-step",new {need});return 8;}
            if(due==null)return heldNavigation==null&&Get(after,"kind") is ("idle" or "welcome" or "security-complete")?0:
                Get(after,"key")==Get(current,"key")?6:Yes(after,"eligible")?7:5;
        }else if(due==null)return 5;
        return await WaitCooldown(due!.Value);
    }
    private async Task Run()
    {
        nint foreground=GetForegroundWindow();FileStream? gate=null;
        try {
            Record("start",new {mode,pid=Environment.ProcessId,fixture,log});
            if(fixture==null) {
                // 与监管.py 发车采用同一字节锁。读账号及点确认期间不能并发发车。
                gate=new FileStream(Path.Combine(Watch,"批次","监管.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
                gate.Lock(0,1); IdleProcesses();
                if(batchFolder!=null&&(!File.Exists(Path.Combine(batchFolder,"票据.json"))||
                    new[]{"E放行.json","消费.json","发车意图.json","worker.json","熔断.json","退出.json","结批.json"}.Any(n=>File.Exists(Path.Combine(batchFolder,n)))))
                    throw new InvalidOperationException("在监管锁内发现票据已关闭、放行或消费，本次拒绝");
            }
            var env=await CoreWebView2Environment.CreateAsync(userDataFolder:profile);
            await web.EnsureCoreWebView2Async(env);
            var core=web.CoreWebView2;
            core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            core.DownloadStarting+=(_,e)=>e.Cancel=true;
            core.PermissionRequested+=(_,e)=>e.State=CoreWebView2PermissionState.Deny;
            // 普通提醒只需读取 HTML/桌信息及一次站内链接。禁用双向连接，避免绕过只读 HTTP 守卫。
            bool passive=mode is "strong" or "fixed";
            if(!passive)await core.AddScriptToExecuteOnDocumentCreatedAsync("Object.defineProperty(window,'WebSocket',{value:class {constructor(){throw new Error('read-only');}},writable:false,configurable:false});");
            if(fixture!=null) core.WebMessageReceived+=(_,e)=>{ if(e.TryGetWebMessageAsString()=="fixture-ack") attempted++; };
            core.NavigationStarting+=(_,e)=>{
                if(!Uri.TryCreate(e.Uri,UriKind.Absolute,out var u)||!AllowedDocument(u)||passive&&u.AbsolutePath is not ("/lobby" or "/penalty")) {
                    e.Cancel=true; heldNavigation=u!=null&&Official(u)?u.AbsolutePath:"other-origin";
                }
            };
            if(passive)core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All);
            else core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested+=(_,e)=>{
                Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var u);
                bool allow=u!=null&&ReadAllowed(e,u);
                if(!allow||fixture!=null) {
                    string? html=allow && fixture!=null ? Fixture.Response(fixture,u!,ref documents,ref attempted):null;
                    if(html==null){
                        blocked++;
                        if(!allow&&u!=null&&blockedSamples.Count<16&&e.ResourceContext is (CoreWebView2WebResourceContext.XmlHttpRequest or CoreWebView2WebResourceContext.Script or CoreWebView2WebResourceContext.Fetch))
                            blockedSamples.Add(e.Request.Method+" "+e.ResourceContext+" "+u.Host+u.AbsolutePath);
                    }
                    e.Response=env.CreateWebResourceResponse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(html??"")),
                        html==null?403:200,html==null?"Read-only":"OK","Content-Type: "+(u?.AbsolutePath.EndsWith(".html")==true?"application/json":"text/html")+"; charset=utf-8\r\nCache-Control: no-store\r\nDate: "+DateTimeOffset.UtcNow.ToString("R"));
                }
            };
            core.WebResourceResponseReceived+=(_,e)=>{
                if(Uri.TryCreate(e.Request.Uri,UriKind.Absolute,out var u)&&AllowedDocument(u)&&e.Response.StatusCode is >=200 and <300 &&
                    e.Response.Headers.Contains("Date")&&DateTimeOffset.TryParse(e.Response.Headers.GetHeader("Date"),out var value)){
                    serverDate=value.ToUniversalTime();serverTick=Environment.TickCount64;
                }
            };
            if(mode=="cooldown-recheck"){
                if(!File.Exists(CooldownFile)){Record("decision",new {reason="没有已记录的冷却"});exit=5;return;}
                using var state=JsonDocument.Parse(File.ReadAllBytes(CooldownFile));
                if(Yes(state.RootElement,"released")){Record("decision",new {reason="此前已记录解除"});exit=0;return;}
                exit=await WaitCooldown(state.RootElement.GetProperty("localDue").GetDateTimeOffset());return;
            }
            if(passive){
                var loaded=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                void Loaded(object? sender,CoreWebView2NavigationCompletedEventArgs e)=>loaded.TrySetResult();
                core.NavigationCompleted+=Loaded;
                try{core.Navigate(Origin+"/lobby");await loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));}
                finally{core.NavigationCompleted-=Loaded;}
            }else core.Navigate(Origin+"/lobby");
            if(mode is "strong" or "fixed"){
                // fixed 仅供离线夹具模拟旧采集器的单次读数；正式 fixed 分支直接启动固定哈希的 ReadOnly.exe。
                bool one=mode=="fixed";
                if(one)await Task.Delay(3500);
                var until=Stopwatch.StartNew(); JsonElement snapshot=default; int cleanSamples=0;
                do {
                    await Task.Delay(one?250:800);
                    try{
                        using var doc=JsonDocument.Parse(await core.ExecuteScriptAsync(snapshotScript+"()").WaitAsync(TimeSpan.FromSeconds(5)));
                        if(doc.RootElement.ValueKind!=JsonValueKind.Object){cleanSamples=0;continue;}
                        snapshot=doc.RootElement.Clone();
                    } catch(COMException){cleanSamples=0;continue;}
                    bool risk=Get(snapshot,"path")=="/penalty"||new[]{"queuedIds","overlays","noticeLines","noticeTables","dialogs","noticeControls"}.Any(k=>snapshot.GetProperty(k).GetArrayLength()>0)||
                        snapshot.GetProperty("credentials").GetInt32()>0||snapshot.GetProperty("progress").EnumerateArray().Any(x=>!string.IsNullOrWhiteSpace(Get(x,"text")));
                    var module=snapshot.GetProperty("lobby");
                    var user=snapshot.GetProperty("user");
                    bool ready=user.ValueKind==JsonValueKind.Object&&Get(user,"id")=="99769903"&&Get(user,"name")=="bke780"&&
                        Get(snapshot,"path")=="/lobby"&&module.ValueKind==JsonValueKind.Object&&Yes(module,"visible")&&module.GetProperty("modes").GetArrayLength()==3&&
                        snapshot.GetProperty("controls").EnumerateArray().Any(c=>Get(c,"id") is ("Xannaplay_1631" or "wannaplay_1631")&&!Yes(c,"disabled")&&
                            System.Text.RegularExpressions.Regex.IsMatch(Get(c,"text")??"","海盐折纸|Sea Salt",System.Text.RegularExpressions.RegexOptions.IgnoreCase)&&
                            System.Text.RegularExpressions.Regex.IsMatch(Get(c,"text")??"","开始游戏|Play now",System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    cleanSamples=ready&&!risk?cleanSamples+1:0;
                    if(one||risk||cleanSamples>=3&&until.ElapsedMilliseconds>=5000)break;
                }while(until.Elapsed<TimeSpan.FromSeconds(65));
                if(snapshot.ValueKind!=JsonValueKind.Object)throw new InvalidDataException("没有可核对的大厅补充只读快照");
                Record("before",snapshot);
                if(heldNavigation!=null)Record("navigation-held",new {path=heldNavigation});
                Record("read-only-complete",new {blockedNavigation=heldNavigation==null?0:1,blockedWrite=blocked,noAction=true,cleanSamples});
                exit=heldNavigation==null?0:5;return;
            }
            var clock=Stopwatch.StartNew();
            var before=await Observe(p=>Get(p,"kind") is not (null or "loading") && (Get(p,"kind")!="idle"||
                clock.ElapsedMilliseconds>=2200&&(mode!="discover"||Yes(p,"readySeaSalt"))));
            Record("before",EvidencePage(before));
            if(heldNavigation!=null){Record("navigation-held",new {path=heldNavigation,hint="官方另有页面待核实，本次未确认"});exit=5;return;}
            if(mode=="discover"){
                if(File.Exists(CooldownFile)){
                    using var state=JsonDocument.Parse(File.ReadAllBytes(CooldownFile));
                    if(!Yes(state.RootElement,"released"))Record("cooldown-pending",new {deadline=state.RootElement.GetProperty("localDue")});
                }
                exit=0;return;
            }
            if(expectedObservation!=null&&Observation(before)!=expectedObservation){
                Record("decision",new {reason="发现时的提醒与本进程复核不同，不自动切换操作对象"});exit=5;return;
            }
            if(Get(before,"kind")=="idle"&&File.Exists(CooldownFile)){
                using var pending=JsonDocument.Parse(File.ReadAllBytes(CooldownFile));
                if(!Yes(pending.RootElement,"released")){
                    var deadline=pending.RootElement.GetProperty("localDue").GetDateTimeOffset();
                    Record("cooldown-pending",new {deadline});
                    exit=mode=="auto"?await WaitCooldown(deadline):9;return;
                }
            }
            if(mode=="auto")RecordFreshClear(before);
            if(Get(before,"kind") is "ordinary-dialog" or "cooldown" or "security"){
                if(requiredTable!=null){Record("decision",new{reason="已限定旧桌，仅处理同桌普通未完赛提醒"});exit=5;return;}
                exit=await HandleDialog(before);return;
            }
            if(Get(before,"kind")!="ordinary") { exit=Get(before,"kind")=="idle"?0:5; return; }
            if((requiredTable!=null&&Get(before,"table")!=requiredTable)||(requiredTime!=null&&Get(before,"when")!=requiredTime)) {
                Record("decision",new {reason="指定桌号或时间不符"});exit=5;return;
            }
            string id=Get(before,"table")!;
            if(Marked(id)) {Record("decision",new {reason="already-marked",table=id});exit=4;return;}
            tableRead=id;
            var request=await Page("request",before);
            if(Get(request,"kind")!="ordinary") {Record("decision",request);exit=5;return;}
            var proof=await Observe(p=>Get(p,"kind")!="reading","proof",before,8);
            tableRead=null;Record("official-read",proof);
            if(!Yes(proof,"verified")){exit=5;return;}
            if(mode=="inspect"){exit=0;return;}
            if(fixture==null) IdleProcesses();
            Claim(proof);
            JsonElement reply=default;
            try {reply=await Page("click",proof);}
            catch(Exception e) when(e is COMException or TimeoutException or JsonException) {Record("click-return",new {kind=e.GetType().Name});}
            Record("action",new {table=id,dispatched=Yes(reply,"dispatched"),reportedCount=Yes(reply,"dispatched")?1:0,markerReserved=true});
            var after=await Observe(p=>Get(p,"path") is "/welcome" or "/" or "/lobby" && Get(p,"hostId")=="99769903" ||
                Get(p,"kind")=="ordinary" && Get(p,"table")!=id || Get(p,"kind") is "cooldown" or "security",seconds:6);
            Record("after",after);
            bool sameAccount=Get(after,"origin")==Origin && Get(after,"hostId")=="99769903" && Get(after,"account")=="bke780";
            exit=!Yes(reply,"dispatched")||!sameAccount||heldNavigation!=null?6:
                Get(after,"kind")=="ordinary"&&Get(after,"table")!=id?7:
                Get(after,"path") is ("/welcome" or "/" or "/lobby") &&
                Get(after,"kind") is ("welcome" or "idle")?0:6;
        } catch(Exception e) {Record("error",new {kind=e.GetType().Name,code=e.HResult,message=e is InvalidOperationException?e.Message:null});exit=6;}
        finally {
            gate?.Dispose();GetWindowThreadProcessId(GetForegroundWindow(),out var pid);
            Record("closed",new {exitCode=exit,blocked,blockedResourceSamples=blockedSamples,fixtureRequests=documents,fixtureActions=attempted,
                foregroundChanged=GetForegroundWindow()!=foreground,foregroundOwned=pid==Environment.ProcessId});Close();
        }
    }
    // 单独启动已验证的只读采集器。此命令只产出大厅观察，永不标记或点确认。
    private static int Recheck(string? directedOutput=null)
    {
        using var gate=new FileStream(Path.Combine(Watch,"批次","监管.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.ReadWrite);
        gate.Lock(0,1);IdleProcesses();
        string dir=Path.Combine(Path.GetTempPath(),"SeaSalt-A-readonly-status-a3286ccc8e1c4f068f109619720b0071","bin","Release","net10.0-windows");
        string exe=Path.Combine(dir,"ReadOnly.exe");
        static string Digest(string path)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path)));
        if(Digest(exe)!=ReaderExe || Digest(Path.Combine(dir,"ReadOnly.dll"))!=ReaderDll)return 6;
        string logs=Path.Combine(Watch,"账号提醒","运行记录");
        if(directedOutput==null)Directory.CreateDirectory(logs);
        string output=directedOutput??Path.Combine(logs,"新进程大厅-"+Guid.NewGuid().ToString("N")+".jsonl");
        if(File.Exists(output))return 6;
        if(directedOutput!=null){
            string folder=Path.GetDirectoryName(output)!;
            if(!File.Exists(Path.Combine(folder,"票据.json"))||new[]{"E放行.json","消费.json","发车意图.json","worker.json","熔断.json","退出.json","结批.json"}.Any(n=>File.Exists(Path.Combine(folder,n))))return 5;
        }
        var start=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true};
        start.ArgumentList.Add(output);using var reader=Process.Start(start)!;
        reader.StandardInput.WriteLine(Origin+"/lobby");reader.StandardInput.Close();
        if(!reader.WaitForExit(45000)){reader.Kill();reader.WaitForExit();return 6;}
        Console.WriteLine(output);
        if(reader.ExitCode!=0)return 6;
        if(directedOutput!=null)return 0;
        foreach(var line in File.ReadLines(output)) {
            using var row=JsonDocument.Parse(line);
            if(Get(row.RootElement,"stage")!="before")continue;
            var page=row.RootElement.GetProperty("page");var user=page.GetProperty("user");
            if(Get(page,"origin")!=Origin||Get(page,"path")!="/lobby"||Get(user,"id")!="99769903"||Get(user,"name")!="bke780" ||
               page.GetProperty("queuedIds").GetArrayLength()!=0||page.GetProperty("overlays").GetArrayLength()!=0||
               page.GetProperty("noticeTables").GetArrayLength()!=0||
               page.GetProperty("noticeLines").EnumerateArray().Any(s=>System.Text.RegularExpressions.Regex.IsMatch(
                   s.GetString()??"","未能完成|禁止匹配|冷却|验证码|安全验证|暂停匹配|继续游戏|返回游戏|轮到你"))||
               page.GetProperty("progress").EnumerateArray().Any(p=>!string.IsNullOrWhiteSpace(Get(p,"text")))||
               page.GetProperty("lobby").ValueKind!=JsonValueKind.Object||!Yes(page.GetProperty("lobby"),"visible"))return 5;
            return 0;
        }
        return 6;
    }
    [STAThread] private static int Main(string[] args)
    {
        if(args.Length>=1 && args[0] is ("--batch" or "--fixture-batch")){
            bool test=args[0]=="--fixture-batch";int o=test?3:1;
            if(args.Length!=o+4)return 2;
            string? scenario=null;string evidence=OfficialEvidence;
            string profile=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SeaSaltDesk","profile");
            if(test){
                scenario=args[1];string full=Path.GetFullPath(args[2]);
                if(!Fixture.Names.Contains(scenario)||!Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase)||
                    !Path.GetFileName(full).StartsWith("SeaSalt-notice-fixture-",StringComparison.Ordinal))return 2;
                evidence=Path.Combine(full,"evidence");profile=Path.Combine(full,"profile");
            }
            string batch=args[o],unique=args[o+1],run=args[o+2],expected=args[o+3];
            if(!System.Text.RegularExpressions.Regex.IsMatch(batch,"^[0-9a-f]{32}$")||!Guid.TryParseExact(unique,"N",out _)||
                run is not ("discover" or "auto" or "cooldown-recheck" or "strong" or "fixed")||(run=="auto"?!System.Text.RegularExpressions.Regex.IsMatch(expected,"^[A-F0-9]{64}$"):expected!="-"))return 2;
            string folder=Path.Combine(evidence,"2026-11-05-hundred","批次监管","批次",batch);
            if(!Directory.Exists(folder))return 2;
            string prefix=run switch {"discover"=>"大厅只读-", "fixed"=>"固定大厅只读-", "strong"=>"补充大厅只读-", _=>"账号处置-"};
            string path=Path.Combine(folder,prefix+unique+".jsonl");
            if(run=="fixed"&&!test){try{return Recheck(path);}catch(Exception e){Console.Error.WriteLine(e.GetType().Name);return 6;}}
            ApplicationConfiguration.Initialize();using var batchForm=new AccountNotice(run,evidence,profile,scenario,null,null,path,run=="auto"?expected:null);
            Application.Run(batchForm);return batchForm.exit;
        }
        if(args is ["recheck"]) { try {return Recheck();}catch(Exception e){Console.Error.WriteLine(e.GetType().Name);return 6;} }
        // 隔离入口强制全请求截获，始终使用临时 profile 与临时 marker，不接触正式目录。
        if(args is ["--fixture",var name,var root,var runMode] && Fixture.Names.Contains(name) && runMode is ("inspect" or "auto" or "cooldown-recheck")) {
            string full=Path.GetFullPath(root), temp=Path.GetFullPath(Path.GetTempPath());
            if(!Path.GetDirectoryName(full)!.Equals(temp.TrimEnd(Path.DirectorySeparatorChar),StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(full).StartsWith("SeaSalt-notice-fixture-",StringComparison.Ordinal))return 2;
            Directory.CreateDirectory(full);ApplicationConfiguration.Initialize();
            using var test=new AccountNotice(runMode,Path.Combine(full,"evidence"),Path.Combine(full,"profile"),name,null,null);
            Application.Run(test);return test.exit;
        }
        if(args.Length!=1&&args.Length!=3 || args[0] is not ("auto" or "inspect" or "cooldown-recheck"))return 2;
        if(args.Length==3&&(!System.Text.RegularExpressions.Regex.IsMatch(args[1],@"^[1-9]\d{0,17}$")||
            !DateTime.TryParseExact(args[2],"yyyy-MM-ddTHH:mm",null,System.Globalization.DateTimeStyles.None,out _)))return 2;
        ApplicationConfiguration.Initialize();
        using var form=new AccountNotice(args[0],OfficialEvidence,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SeaSaltDesk","profile"),
            null,args.Length==3?args[1]:null,args.Length==3?args[2]:null);
        Application.Run(form);return form.exit;
    }
}
