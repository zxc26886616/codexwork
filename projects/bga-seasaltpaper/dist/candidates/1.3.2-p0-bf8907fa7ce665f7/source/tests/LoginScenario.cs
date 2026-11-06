using System.Text;
using Microsoft.Web.WebView2.Core;
using SeaSaltDesk;

internal static class LoginScenario
{
    // Isolated WebView: all resource requests are answered locally, including on errors.
    private const string Page = """
        <!doctype html><meta charset="utf-8"><div id="account-module">
        <section class="bga-account-manager-form__step1"><form>
        <input name="email"><a class="bga-button--blue" style="pointer-events:none" href="#">Next</a></form></section>
        <section class="bga-account-manager-form__step2" style="opacity:0;pointer-events:none"><form>
        <input autocomplete="username" readonly><input type="password">
        <a class="bga-button--blue" href="#">Login</a></form></section></div>
        <script>
        const [one,two] = document.querySelectorAll("section");
        const [name,echo,pass] = document.querySelectorAll("input");
        let boundPassword="";
        pass.oninput=()=>queueMicrotask(()=>boundPassword=pass.value);
        name.oninput=()=>setTimeout(()=>one.querySelector("a").removeAttribute("style"),150);
        document.querySelectorAll("a")[0].onclick=e=>{
           e.preventDefault();if(name.value!=="desk-test")return;
           echo.value=name.value;one.hidden=true;two.removeAttribute("style");
        };
        document.querySelectorAll("a")[1].onclick=e=>{
           e.preventDefault();sessionStorage.count=Number(sessionStorage.count||0)+1;
           if(boundPassword==="无效测试口令"){pass.form.insertAdjacentHTML("beforeend",'<div class="text-bga-input-red">Rejected</div>');return;}
           if(boundPassword!=='海"testing') throw Error("读取到尚未同步的密码");
           location.replace("/lobby");
        };
        </script>
        """;

    private static async Task Verify(string root, bool reject)
    {
        var vault = new LoginVault(root);
        var site = reject ? LoginSite.BGA : LoginSite.Studio;
        string origin = reject ? "https://en.boardgamearena.com" : "https://studio.boardgamearena.com";
        var secret = new LoginSecret("desk-test", reject ? "无效测试口令" : "海\"testing");
        try
        {
            if (reject) vault.Save(site, secret);
            using var desk = new Desk(root) { Opacity = 0, ShowInTaskbar = false };
            var controls = Checks.Children(desk).ToList();
            var web = controls.OfType<TableBrowser>().Single();
            var status = controls.OfType<Label>().Single(l => l.Name == "status");
            web.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                var core = web.CoreWebView2;
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, req) =>
                {
                    var uri = new Uri(req.Request.Uri);
                    string? body = uri.GetLeftPart(UriPartial.Authority) != origin ? null : uri.AbsolutePath switch {
                        "/account" => Page + (reject ? "" : "<div id='didomi-notice'>Cookie</div>"),
                        "/lobby" => "<script>window.globalUserInfos={id:42,name:'desk-test'}</script><p>测试大厅</p>",
                        _ => null
                    };
                    req.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(Encoding.UTF8.GetBytes(body ?? "")), body == null ? 404 : 200,
                        body == null ? "Not Found" : "OK", "Content-Type: text/html; charset=utf-8");
                };
            };
            desk.Show();
            async Task WaitFor(Func<bool> ready, string step)
            {
                var end = DateTime.UtcNow.AddSeconds(25);
                while (!ready())
                {
                    if (DateTime.UtcNow >= end) throw new Exception($"{step}超时（{web.Source?.AbsolutePath}），状态为“{status.Text}”。");
                    await Task.Delay(100);
                }
            }
            if (!reject)
            {
                var setup = controls.OfType<Button>().Single(b => b.Text == "自动登录");
                await WaitFor(() => setup.Enabled, "等待账号设置");
                // PerformClick opens a modal dialog and pumps this queued action on the UI thread.
                Exception? error = null;
                desk.BeginInvoke(() =>
                {
                    var dialog = Application.OpenForms.Cast<Form>().SingleOrDefault(f => f.Owner == desk);
                    try
                    {
                        if (dialog == null) throw new Exception("未打开自动登录设置窗口。");
                        var fields = Checks.Children(dialog).ToList();
                        fields.OfType<ComboBox>().Single().SelectedItem = site;
                        fields.OfType<TextBox>().Single(t => !t.UseSystemPasswordChar).Text = secret.User;
                        fields.OfType<TextBox>().Single(t => t.UseSystemPasswordChar).Text = secret.Password;
                        fields.OfType<Button>().Single(b => b.Text == "保存并登录").PerformClick();
                        if (dialog.DialogResult != DialogResult.OK) throw new Exception("账号设置未能保存。");
                    }
                    catch (Exception ex) { error = ex; }
                    finally { dialog?.Close(); }
                });
                setup.PerformClick();
                if (error != null) throw error;
                await WaitFor(() => status.Text.Contains("Cookie"), "等待 Cookie 偏好");
                if (await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[name=email]').value === ''") != "true")
                    throw new Exception("选择 Cookie 偏好前已填写用户名。");
                await web.CoreWebView2.ExecuteScriptAsync("document.getElementById('didomi-notice').remove()");
            }
            string expected = reject ? "BGA 拒绝" : "Studio 已登录";
            await WaitFor(() => status.Text.Contains(expected), "等待登录结果");
            var log = controls.OfType<RichTextBox>().Single(t => t.AccessibleName == "操作日志内容").Text;
            if (!log.Contains(expected) || log.Contains(secret.User) || log.Contains(secret.Password))
                throw new Exception("操作日志缺少登录结果，或包含了账号或密码。");
            if (vault.Preferred != site || vault.Read(site)?.Password != secret.Password ||
                vault.Read(reject ? LoginSite.Studio : LoginSite.BGA) != null)
                throw new Exception("保存的凭据站点或密码不符。");
            if (await web.CoreWebView2.ExecuteScriptAsync("Number(sessionStorage.count)") != "1")
                throw new Exception("密码提交次数不等于一次。");
            if (reject)
            {
                await web.CoreWebView2.ExecuteScriptAsync("""
                    document.querySelector('.text-bga-input-red').remove();
                    document.querySelector('[type=password]').value='';
                    document.body.insertAdjacentHTML("beforeend",'<input autocomplete="one-time-code">');
                    """);
                if (await web.LoginStepAsync(origin, "pass", secret.User, secret.Password) != "challenge")
                    throw new Exception("验证码出现时仍允许填写密码。");
                await web.CoreWebView2.ExecuteScriptAsync("""
                    document.querySelector('[autocomplete=one-time-code]').remove();
                    document.querySelector('[type=password]').form.action='https://example.invalid/';
                    """);
                if (await web.LoginStepAsync(origin, "pass", secret.User, secret.Password) != "wrong-origin")
                    throw new Exception("密码表单跨站时仍允许填写密码。");
                await web.CoreWebView2.ExecuteScriptAsync("""
                    document.querySelector('[type=password]').form.removeAttribute('action');
                    document.querySelector('[autocomplete=username]').value='someone-else';
                    """);
                if (await web.LoginStepAsync(origin, "pass", secret.User, secret.Password) != "other-account" ||
                    await web.LoginStepAsync("https://studio.boardgamearena.com", "pass", secret.User, secret.Password) != "wrong-origin" ||
                    await web.CoreWebView2.ExecuteScriptAsync("document.querySelector('[type=password]').value === '' && Number(sessionStorage.count) === 1") != "true")
                    throw new Exception("密码页账号或站点不匹配时仍写入或提交了密码。");
            }
        }
        finally { vault.Forget(site); }
        if (vault.Read(site) != null || vault.Preferred != null) throw new Exception("忘记账号后仍能读取凭据或默认站点。");
    }

    public static async Task Run(string temp)
    {
        await Verify(Path.Combine(temp, "login-ok"), false);
        await Verify(Path.Combine(temp, "login-rejected"), true);
        Console.WriteLine("通过 · 桌面自动登录：首次保存、启动登录、两步表单、登录成功、拒绝后停止、Cookie 偏好、验证码、账号与站点隔离、忘记凭据。");
    }

    public static async Task CheckPublicPages(string temp)
    {
        using var form = new Form { Opacity = 0, ShowInTaskbar = false };
        var web = new TableBrowser { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        form.Show();
        await web.InitializeAsync(Path.Combine(temp, "anonymous-login-profile"));
        foreach (var host in new[] { "en.boardgamearena.com", "studio.boardgamearena.com" })
        {
            var origin = "https://" + host;
            web.Source = new Uri(origin + "/account?warn");
            var end = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                var stage = await web.LoginStepAsync(origin, "probe", "");
                if (stage is "user" or "cookies" && await web.CoreWebView2.ExecuteScriptAsync("""
                    !!document.querySelector('#account-module .bga-account-manager-form__step1 form input[name="email"]') &&
                    !!document.querySelector('#account-module .bga-account-manager-form__step1 form a.bga-button--blue[href="#"]')
                    """) == "true") break;
                if (DateTime.UtcNow >= end) throw new Exception($"未能识别 {host} 的匿名登录表单。");
                await Task.Delay(400);
            }
            Console.WriteLine($"通过 · 官方匿名登录页：{host} 已显示用户名表单，仅检查页面，未填写或提交凭据。");
        }
    }
}
