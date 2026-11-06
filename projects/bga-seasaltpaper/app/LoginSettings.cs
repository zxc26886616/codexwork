namespace SeaSaltDesk;

internal sealed class LoginSettings : Form
{
    public LoginSite SelectedSite => (LoginSite)sites.SelectedItem!;
    private readonly ComboBox sites = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox user = new() { Dock = DockStyle.Fill };
    private readonly TextBox password = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true, MaxLength = 1280 };
    private readonly Label hint = new() { Dock = DockStyle.Fill };
    public LoginSettings(LoginVault vault)
    {
        Text = "自动登录";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(420, 225);
        var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 2, RowCount = 5 };
        panel.ColumnStyles.Add(new(SizeType.AutoSize));
        panel.ColumnStyles.Add(new(SizeType.Percent, 100));
        Controls.Add(panel);
        Control[] fields = [sites, user, password];
        string[] labels = ["站点", "用户名", "密码"];
        for (int i = 0; i < fields.Length; i++)
        {
            panel.Controls.Add(new Label { Text = labels[i], AutoSize = true }, 0, i);
            panel.Controls.Add(fields[i], 1, i);
        }
        panel.Controls.Add(hint, 0, 3);
        panel.SetColumnSpan(hint, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var login = new Button { Text = "保存并登录", AutoSize = true };
        var forget = new Button { Text = "忘记此站账号", AutoSize = true };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
        actions.Controls.AddRange([login, forget, cancel]);
        panel.Controls.Add(actions, 0, 4);
        panel.SetColumnSpan(actions, 2);
        AcceptButton = login;
        CancelButton = cancel;
        void RefreshSite()
        {
            user.Clear();
            password.Clear();
            password.PlaceholderText = "";
            login.Enabled = forget.Enabled = false;
            var saved = vault.Read(SelectedSite);
            user.Text = saved?.User ?? "";
            password.PlaceholderText = saved == null ? "" : "已保存，留空继续使用";
            hint.Text = "密码保存在 Windows 凭据管理器中。下次启动时，助手会登录所选站点。";
            login.Enabled = true;
            forget.Enabled = saved != null;
        }
        sites.Items.AddRange([LoginSite.BGA, LoginSite.Studio]);
        sites.SelectedIndexChanged += (_, _) =>
        {
            try { RefreshSite(); }
            catch (System.ComponentModel.Win32Exception) { hint.Text = "无法读取此站账号，请检查 Windows 凭据管理器。"; }
        };
        sites.SelectedItem = vault.Preferred ?? LoginSite.BGA;
        login.Click += (_, _) =>
        {
            try
            {
                string name = user.Text.Trim();
                var saved = vault.Read(SelectedSite);
                string pass = password.Text;
                if (pass.Length == 0 && saved?.User == name) pass = saved.Password;
                vault.Save(SelectedSite, new(name, pass));
                DialogResult = DialogResult.OK;
            }
            catch (ArgumentException) { hint.Text = "请输入用户名与密码。更换用户名时需要重新输入密码。"; }
            catch (Exception) { hint.Text = "保存失败，请检查 Windows 凭据管理器和助手数据目录。"; }
        };
        forget.Click += (_, _) =>
        {
            try { vault.Forget(SelectedSite); RefreshSite(); hint.Text = "已移除本机保存的登录信息。"; }
            catch (Exception) { hint.Text = "移除失败，请检查 Windows 凭据管理器和助手数据目录。"; }
        };
    }
}
