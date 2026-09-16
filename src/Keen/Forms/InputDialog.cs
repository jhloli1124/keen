namespace Keen.Forms;

// 极简模态文本输入(#7 版本备注用)。
internal sealed class InputDialog : Form
{
    private readonly TextBox _box = new();
    public string Value => _box.Text;

    public InputDialog(string title, string label, string? initial)
    {
        Text = title;
        Width = 420;
        Height = 180;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        var lbl = new Label
        {
            Text = label,
            Left = 12,
            Top = 12,
            AutoSize = true,
        };
        _box.Left = 12;
        _box.Top = 40;
        _box.Width = 380;
        _box.Text = initial ?? "";
        _box.Select(_box.Text.Length, 0);
        var ok = new Button
        {
            Text = "确定",
            Left = 196,
            Top = 80,
            Width = 90,
            DialogResult = DialogResult.OK,
        };
        var cancel = new Button
        {
            Text = "取消",
            Left = 294,
            Top = 80,
            Width = 90,
            DialogResult = DialogResult.Cancel,
        };

        AcceptButton = ok;
        CancelButton = cancel;
        Controls.AddRange(new Control[] { lbl, _box, ok, cancel });
    }
}
