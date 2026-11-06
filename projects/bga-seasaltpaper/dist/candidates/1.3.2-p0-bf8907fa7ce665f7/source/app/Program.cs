namespace SeaSaltDesk;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args is ["--build-identity"]) { BuildIdentity.Write(); return; }
        ApplicationConfiguration.Initialize();
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SeaSaltDesk");
        Uri? firstPage = null;
        if (args.Length > 0)
        {
            try { firstPage = TableBrowser.Address(args[0]); }
            catch (ArgumentException)
            {
                MessageBox.Show("启动参数应为 BGA 的 HTTPS 链接或纯数字桌号。", "海盐折纸桌面助手");
                return;
            }
        }
        Application.Run(new Desk(root, firstPage));
    }
}
