using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

// 设置窗(#2):保险库位置(重启生效,不自动迁移)、去重、保留策略、失败通知;立即清理一次。
internal sealed class SettingsForm : Form
{
    private readonly ConfigService _config;
    private readonly BackupPipeline _pipeline;
    private readonly RetentionService _retention;

    private readonly NumericUpDown _keep = new()
    {
        Minimum = 0,
        Maximum = 100000,
        Width = 80,
    };
    private readonly NumericUpDown _age = new()
    {
        Minimum = 0,
        Maximum = 100000,
        Width = 80,
    };
    private readonly TextBox _winMerge = new() { Width = 300 };
    private readonly Button _browseWm = new() { Text = "浏览…", AutoSize = true };
    private readonly CheckBox _notify = new()
    {
        Text = "捕获失败时弹 Windows 通知",
        AutoSize = true,
    };
    private readonly Label _vaultLabel = new() { AutoSize = true };
    private readonly Button _changeVault = new() { Text = "更改…", AutoSize = true };
    private readonly Button _pruneNow = new() { Text = "立即按策略清理一次", AutoSize = true };
    private readonly Button _ok = new() { Text = "确定", Width = 120 };
    private string? _newVault;

    public SettingsForm(IServiceProvider sp)
    {
        _config = sp.GetRequiredService<ConfigService>();
        _pipeline = sp.GetRequiredService<BackupPipeline>();
        _retention = sp.GetRequiredService<RetentionService>();

        Text = "Keen · 设置";
        Width = 560;
        Height = 520;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;

        BuildUi();
        Load += (_, _) => LoadValues();
    }

    private void BuildUi()
    {
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Padding = new Padding(16),
            AutoScroll = true,
        };

        var vaultRow = new FlowLayoutPanel { AutoSize = true };
        vaultRow.Controls.Add(
            new Label
            {
                Text = "保险库位置：",
                AutoSize = true,
                Margin = new Padding(0, 8, 6, 0),
            }
        );
        vaultRow.Controls.Add(_vaultLabel);
        vaultRow.Controls.Add(_changeVault);
        _changeVault.Click += ChangeVault;

        var keepRow = new FlowLayoutPanel { AutoSize = true };
        keepRow.Controls.Add(
            new Label
            {
                Text = "每文件最多保留",
                AutoSize = true,
                Margin = new Padding(0, 6, 4, 0),
            }
        );
        keepRow.Controls.Add(_keep);
        keepRow.Controls.Add(
            new Label
            {
                Text = " 版（0 = 不限）",
                AutoSize = true,
                Margin = new Padding(4, 6, 0, 0),
            }
        );

        var ageRow = new FlowLayoutPanel { AutoSize = true };
        ageRow.Controls.Add(
            new Label
            {
                Text = "清理超过",
                AutoSize = true,
                Margin = new Padding(0, 6, 4, 0),
            }
        );
        ageRow.Controls.Add(_age);
        ageRow.Controls.Add(
            new Label
            {
                Text = " 天的版本（0 = 不限）",
                AutoSize = true,
                Margin = new Padding(4, 6, 0, 0),
            }
        );

        var wmRow = new FlowLayoutPanel { AutoSize = true };
        wmRow.Controls.Add(
            new Label
            {
                Text = "WinMerge 路径：",
                AutoSize = true,
                Margin = new Padding(0, 6, 6, 0),
            }
        );
        wmRow.Controls.Add(_winMerge);
        wmRow.Controls.Add(_browseWm);
        _browseWm.Click += (_, _) =>
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "WinMergeU.exe|WinMergeU.exe|可执行文件 (*.exe)|*.exe",
                Title = "选择 WinMergeU.exe",
            };
            if (ofd.ShowDialog(this) == DialogResult.OK)
                _winMerge.Text = ofd.FileName;
        };

        _pruneNow.Click += async (_, _) =>
        {
            // 只按当前 UI 值跑清理,不保存任何设置(保存只属于「确定」;X = 取消的语义不能被这个按钮破坏)
            await _retention.RunAsync((int)_keep.Value, (int)_age.Value);
            MessageBox.Show("已按当前策略清理一次（未保存设置）。", "Keen");
        };
        _ok.Click += async (_, _) =>
        {
            await SaveAsync();
            DialogResult = DialogResult.OK;
            Close();
        };

        panel.Controls.Add(vaultRow);
        panel.Controls.Add(_notify);
        panel.Controls.Add(keepRow);
        panel.Controls.Add(ageRow);
        panel.Controls.Add(wmRow);
        panel.Controls.Add(_pruneNow);
        panel.Controls.Add(_ok);
        Controls.Add(panel);
    }

    private void LoadValues()
    {
        _notify.Checked = _config.Current.NotifyOnFailure;
        _keep.Value = Clamp(_config.Current.RetainKeepLast);
        _age.Value = Clamp(_config.Current.RetainMaxAgeDays);
        _vaultLabel.Text = _config.Current.VaultRoot;
        _winMerge.Text = _config.Current.WinMergePath ?? "";
    }

    private static decimal Clamp(int v) => v < 0 ? 0 : (v > 100000 ? 100000 : v);

    private void ChangeVault(object? s, EventArgs e)
    {
        using var fbd = new FolderBrowserDialog { SelectedPath = _config.Current.VaultRoot };
        if (fbd.ShowDialog(this) != DialogResult.OK)
            return;
        _newVault = fbd.SelectedPath;
        _vaultLabel.Text = _newVault + "  （重启生效）";
    }

    private async Task SaveAsync()
    {
        _config.Current.NotifyOnFailure = _notify.Checked;
        _config.Current.RetainKeepLast = (int)_keep.Value;
        _config.Current.RetainMaxAgeDays = (int)_age.Value;
        _config.Current.WinMergePath = string.IsNullOrWhiteSpace(_winMerge.Text)
            ? null
            : _winMerge.Text;
        var vaultChanged = _newVault is not null;
        if (vaultChanged)
            _config.Current.VaultRoot = _newVault!;
        await _config.SaveAsync();

        if (vaultChanged)
            MessageBox.Show(
                "保险库位置已记录，下次启动生效。\n旧保险库保留在原位；如需迁移，请手动把旧目录里的 keen.sqlite 与子文件夹复制到新位置。",
                "Keen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information
            );
    }
}
