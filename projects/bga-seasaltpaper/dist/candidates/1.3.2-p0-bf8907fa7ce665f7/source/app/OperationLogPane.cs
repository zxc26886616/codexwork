namespace SeaSaltDesk;

internal sealed class OperationLogPane : GroupBox
{
    private const int Capacity = 200;
    private readonly RichTextBox entries = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, DetectUrls = false, WordWrap = true,
        ScrollBars = RichTextBoxScrollBars.Vertical, BorderStyle = BorderStyle.None,
        BackColor = SystemColors.Window, AccessibleName = "操作日志内容"
    };
    private int count;
    private string? lastMessage;

    public OperationLogPane()
    {
        Text = "操作日志";
        Padding = new Padding(8);
        Controls.Add(entries);
    }

    public bool Record(string message, bool action)
    {
        if (message.Length == 0 || (!action && message == lastMessage)) return false;
        lastMessage = message;
        if (count == Capacity)
        {
            string text = entries.Text;
            entries.Text = text[(text.IndexOf('\n') + 1)..];
        }
        else count++;
        entries.AppendText($"{DateTime.Now:HH:mm:ss}　{message}\n");
        entries.SelectionStart = entries.TextLength;
        entries.ScrollToCaret();
        return true;
    }
}
