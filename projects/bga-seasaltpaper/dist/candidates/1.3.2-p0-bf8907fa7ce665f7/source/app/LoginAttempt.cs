namespace SeaSaltDesk;

internal sealed class LoginAttempt(LoginSite site, LoginSecret secret)
{
    private bool sentUser, sentPassword;
    private DateTime deadline = DateTime.UtcNow.AddSeconds(45);
    public string Origin => site == LoginSite.Studio ? TableBrowser.StudioOrigin : TableBrowser.PlayerOrigin;
    public Uri Destination => new(Origin + (site == LoginSite.Studio ? "/lobby" : "/gamepanel?game=seasaltpaper"));
    public Uri Entry => new(Origin + "/account?warn&redirect=" + Uri.EscapeDataString(Destination.PathAndQuery));

    public async Task<(bool Done, bool Success, string Message)> Tick(TableBrowser web, Func<bool>? isCurrent = null)
    {
        string stage = await web.LoginStepAsync(Origin, "probe", secret.User);
        switch (stage)
        {
            case "signed-in": return (true, true, $"{site} 已登录");
            case "other-account": return (true, false, "页面账号与已保存用户名不一致，请核对自动登录设置。");
            case "wrong-origin": return (true, false, "已离开所选登录站点，自动登录已停止。");
            case "unknown-user": return (true, false, $"{site} 提示用户名未注册，尚未提交密码。");
            case "rejected": return (true, false, sentPassword ? "BGA 拒绝了登录信息，请核对账户或查看页面提示。" :
                $"{site} 登录页提示用户名有误，尚未提交密码。");
            case "cookies":
            case "challenge":
                deadline = DateTime.UtcNow.AddSeconds(45);
                return (false, false, stage == "cookies" ? "请在页面上选择 Cookie 偏好，助手会继续登录。" : "请在页面上完成安全验证，助手会继续登录。");
        }
        if (DateTime.UtcNow > deadline)
            return (true, false, sentPassword ? "密码已提交，站点未确认登录。请打开桌面助手查看登录页。" :
                sentUser ? "用户名已提交，站点未显示此账号的密码页。请在桌面助手中核对站点和账号。" :
                "未能识别可提交的登录表单，尚未提交用户名或密码。请打开桌面助手查看登录页。");
        if (!sentPassword && (stage == "pass" || stage == "user" && !sentUser) && isCurrent?.Invoke() != false)
        {
            // 发起提交后，页面有可能先跳转再返回脚本结果。仅在页面明确尚未点击时允许重试。
            bool userStep = stage == "user";
            if (userStep) sentUser = true;
            else sentPassword = true;
            var reply = await web.LoginStepAsync(Origin, stage, secret.User, stage == "user" ? secret.User : secret.Password);
            bool submitted = reply == "sent-" + stage || reply == "unconfirmed";
            if (userStep) sentUser = submitted;
            else sentPassword = submitted;
            if (submitted) deadline = DateTime.UtcNow.AddSeconds(45);
        }
        return (false, false, sentPassword ? "密码已提交，正在等待 BGA 确认。" :
            sentUser ? "用户名已提交，正在等待密码页。" : "正在识别 BGA 官方登录表单…");
    }
}
