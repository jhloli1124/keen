using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Keen.Models;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

internal sealed class MainForm : Form
{
    private readonly BackupPipeline _pipeline;
    private readonly VaultIndex _index;
    private readonly WatchService _watches;
    private readonly IServiceProvider _sp;

    private readonly DataGridView _grid = new();
    private readonly BindingList<WatchedRow> _rows = new();
    private readonly Button _add = new() { Text = "添加文件…" };
    private readonly Button _remove = new() { Text = "停止监控" };
    private readonly Button _open = new() { Text = "打开" };
    private readonly Button _history = new() { Text = "历史…" };
    private readonly Button _reactivate = new() { Text = "重新监控" };
    private readonly Button _purge = new() { Text = "彻底删除" };
    private readonly Button _stats = new() { Text = "统计" };

    public bool AllowClose;

    public MainForm(IServiceProvider sp)
    {
        _sp = sp;
        _pipeline = sp.GetRequiredService<BackupPipeline>();
        _index = sp.GetRequiredService<VaultIndex>();
        _watches = sp.GetRequiredService<WatchService>();

        Text = "Keen · 被监控文件";
        Width = 980;
        Height = 580;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        BuildUi();

        _pipeline.VersionCaptured += OnVersionCaptured;
        _pipeline.HealthChanged += OnHealthChanged;
        _watches.FileAdded += OnFileAdded;
        _watches.FileReactivated += OnFileReactivated;
        _watches.FileDeactivated += OnFileDeactivated;
        _watches.FileRemoved += OnFileRemoved;
        _watches.FileMovedUi += OnFileMovedUi;

        Load += async (_, _) => await LoadRowsAsync();
    }

    private void BuildUi()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 0),
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            AutoScroll = true,
        };
        foreach (var b in new[] { _add, _remove, _open, _history, _reactivate, _purge, _stats })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            b.AutoSize = true;
            bar.Controls.Add(b);
        }

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = false;
        _grid.EnableHeadersVisualStyles = false; // #2 表头跟随深/浅色
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 90, 158);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        // 锁死：禁止手动调整行高与列头高
        _grid.AllowUserToResizeRows = false;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        AddCol("文件", nameof(WatchedRow.DisplayName), 160);
        AddCol("路径", nameof(WatchedRow.Path), 300);
        AddCol("状态", nameof(WatchedRow.HealthDisplay), 70);
        AddCol("上次存版", nameof(WatchedRow.LastCapturedDisplay), 150);
        AddCol("版本数", nameof(WatchedRow.VersionCount), 60);
        AddCol("大小", nameof(WatchedRow.SizeDisplay), 80);
        _grid.DataSource = _rows;

        Controls.Add(_grid);
        Controls.Add(bar);

        _add.Click += async (_, _) => await AddAsync();
        _remove.Click += async (_, _) => await RemoveAsync();
        _open.Click += (_, _) => OpenSelected();
        _history.Click += (_, _) => HistorySelected();
        _reactivate.Click += async (_, _) => await ReactivateAsync();
        _purge.Click += async (_, _) => await PurgeAsync();
        _stats.Click += async (_, _) => await StatsAsync();
        _grid.SelectionChanged += (_, _) => UpdateButtonStates();
        _grid.CellDoubleClick += (_, e) => OnCellDoubleClicked(e);

        // #5 拖拽:把文件拖进主窗直接添加
        AllowDrop = true;
        DragEnter += (_, e) =>
            e.Effect =
                e.Data?.GetDataPresent(DataFormats.FileDrop) == true
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
        DragDrop += async (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files)
                return;
            var rejected = new List<string>();
            foreach (var f in files)
            {
                var wf = await _watches.AddFileAsync(f);
                if (wf is null)
                    rejected.Add(f);
            }
            if (rejected.Count > 0)
                MessageBox.Show(
                    this,
                    "未能添加（路径不支持、在保险库内部或非法路径）：\n"
                        + string.Join("\n", rejected),
                    "Keen",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
        };

        UpdateButtonStates();
    }

    private void AddCol(string header, string prop, int width) =>
        _grid.Columns.Add(
            new DataGridViewTextBoxColumn
            {
                HeaderText = header,
                DataPropertyName = prop,
                Name = prop,
                Width = width,
                FillWeight = width,
            }
        );

    private Guid SelectedGuid() =>
        (_grid.CurrentRow?.DataBoundItem as WatchedRow)?.Guid ?? Guid.Empty;

    private void UpdateButtonStates()
    {
        var row = _grid.CurrentRow?.DataBoundItem as WatchedRow;
        if (row is null)
        {
            _remove.Enabled =
                _open.Enabled =
                _history.Enabled =
                _reactivate.Enabled =
                _purge.Enabled =
                    false;
            return;
        }
        _remove.Enabled = _open.Enabled = row.IsActive;
        _history.Enabled = true;
        _reactivate.Enabled = !row.IsActive;
        _purge.Enabled = !row.IsActive;
    }

    private async Task LoadRowsAsync()
    {
        var files = await _index.LoadAllWatchedFilesAsync();
        foreach (var wf in files)
        {
            if (FindRowByPath(wf.CurrentPath) is not null)
                continue; // 加载期间 FileAdded 可能已加过该行
            var count = await _index.CountVersionsAsync(wf.Guid);
            var last = await _index.GetLastVersionAsync(wf.Guid);
            _rows.Add(
                new WatchedRow
                {
                    Guid = wf.Guid,
                    DisplayName = wf.DisplayName,
                    Path = wf.CurrentPath,
                    IsActiveField = wf.IsActive,
                    VersionCountField = count,
                    LastCapturedTicksField = last?.CapturedAtTicks ?? 0,
                    SizeBytesField = last?.SizeBytes ?? 0,
                    HealthField = FileHealth.Watching,
                }
            );
        }
        RefreshRowStyles();
        UpdateButtonStates();
    }

    // 已移除的行灰显。
    private void RefreshRowStyles()
    {
        foreach (DataGridViewRow r in _grid.Rows)
        {
            if (r.DataBoundItem is WatchedRow wr)
                r.DefaultCellStyle.ForeColor = wr.IsActive ? Color.Empty : Color.Gray;
        }
    }

    private void OnVersionCaptured(VersionEntry e) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(e.WatchedGuid);
            if (row is null)
                return;
            row.VersionCountField += 1;
            row.LastCapturedTicksField = e.CapturedAtTicks;
            row.SizeBytesField = e.SizeBytes;
            row.HealthField = FileHealth.Watching;
        });

    private void OnHealthChanged(Guid guid, FileHealth h, string? msg) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(guid);
            if (row is not null)
                row.HealthField = h;
        });

    private void OnFileAdded(WatchedFile wf) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            if (FindRowByPath(wf.CurrentPath) is not null)
                return;
            _rows.Add(
                new WatchedRow
                {
                    Guid = wf.Guid,
                    DisplayName = wf.DisplayName,
                    Path = wf.CurrentPath,
                    IsActiveField = true,
                    HealthField = FileHealth.Watching,
                }
            );
            RefreshRowStyles();
        });

    private void OnFileReactivated(WatchedFile wf) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(wf.Guid) ?? FindRowByPath(wf.CurrentPath);
            if (row is null)
                return;
            row.IsActiveField = true;
            row.HealthField = FileHealth.Watching;
            RefreshRowStyles();
            UpdateButtonStates();
        });

    private void OnFileDeactivated(Guid guid) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(guid);
            if (row is not null)
                row.IsActiveField = false;
            RefreshRowStyles();
            UpdateButtonStates();
        });

    private void OnFileRemoved(Guid guid) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(guid);
            if (row is not null)
                _rows.Remove(row);
        });

    // 被监控文件被同目录改名:更新行显示。
    private void OnFileMovedUi(Guid guid, string newPath, string name) =>
        BeginInvoke(() =>
        {
            if (IsDisposed)
                return;
            var row = FindRow(guid);
            if (row is null)
                return;
            row.DisplayName = name;
            row.Path = newPath;
        });

    private WatchedRow? FindRow(Guid g) => _rows.FirstOrDefault(r => r.Guid == g);

    private WatchedRow? FindRowByPath(string p) =>
        _rows.FirstOrDefault(r => string.Equals(r.Path, p, StringComparison.OrdinalIgnoreCase));

    private async Task AddAsync()
    {
        using var ofd = new OpenFileDialog { Multiselect = true, Title = "选择要监控的文件" };
        if (ofd.ShowDialog(this) != DialogResult.OK)
            return;
        foreach (var path in ofd.FileNames)
        {
            var wf = await _watches.AddFileAsync(path);
            if (wf is null)
                MessageBox.Show(
                    this,
                    $"未能添加（可能是不支持的路径、在保险库内部，或非法路径）：\n{path}",
                    "Keen",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
        }
    }

    private async Task RemoveAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row)
            return;
        var ok = MessageBox.Show(
            this,
            $"移除对该文件的监控？\n历史版本会保留（主窗里灰显，「历史」仍可用）。\n{row.Path}",
            "Keen",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question
        );
        if (ok != DialogResult.OK)
            return;
        try
        {
            await _watches.RemoveAsync(row.Guid);
        } // FileDeactivated 会把行置灰
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "停止监控失败：" + ex.Message,
                "Keen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private async Task ReactivateAsync()
    {
        var guid = SelectedGuid();
        if (guid == Guid.Empty)
            return;
        try
        {
            await _watches.ReactivateAsync(guid);
        } // FileReactivated 会恢复行
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "重新监控失败：" + ex.Message,
                "Keen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private async Task PurgeAsync()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row)
            return;
        var ok = MessageBox.Show(
            this,
            $"彻底删除该文件的全部历史版本？\n此操作不可撤销。\n{row.DisplayName}",
            "Keen",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning
        );
        if (ok != DialogResult.OK)
            return;
        try
        {
            await _watches.PurgeAsync(row.Guid);
        } // FileRemoved 会移除行
        catch (Exception ex)
        {
            MessageBox.Show(
                this,
                "彻底删除失败：" + ex.Message,
                "Keen",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning
            );
        }
    }

    private void OpenSelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(row.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开失败：" + ex.Message, "Keen");
        }
    }

    private void HistorySelected()
    {
        if (_grid.CurrentRow?.DataBoundItem is not WatchedRow row)
            return;
        new HistoryForm(row.Guid, row.DisplayName, _sp).Show();
    }

    // #4 双击文件列→历史;双击路径列→打开所在文件夹。
    private void OnCellDoubleClicked(DataGridViewCellEventArgs e)
    {
        if (e.RowIndex < 0)
            return;
        var row = _grid.Rows[e.RowIndex].DataBoundItem as WatchedRow;
        if (row is null)
            return;
        var col = _grid.Columns[e.ColumnIndex].Name;
        if (col == nameof(WatchedRow.DisplayName))
            HistorySelected();
        else if (col == nameof(WatchedRow.Path))
            OpenFolder(row.Path);
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(
                new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true,
                }
            );
        }
        catch { }
    }

    // #13 统计:被监控文件数、版本总数、版本合计大小。
    private async Task StatsAsync()
    {
        var files = await _index.LoadAllWatchedFilesAsync();
        var (count, size) = await _index.GetTotalsAsync();
        MessageBox.Show(
            this,
            $"被监控文件（含已移除）：{files.Count}\n版本总数：{count}\n版本合计大小：{VersionRow.FormatBytes(size)}",
            "Keen · 统计",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information
        );
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        _pipeline.VersionCaptured -= OnVersionCaptured;
        _pipeline.HealthChanged -= OnHealthChanged;
        _watches.FileAdded -= OnFileAdded;
        _watches.FileReactivated -= OnFileReactivated;
        _watches.FileDeactivated -= OnFileDeactivated;
        _watches.FileRemoved -= OnFileRemoved;
        _watches.FileMovedUi -= OnFileMovedUi;
        base.OnFormClosing(e);
    }
}

internal sealed class WatchedRow : INotifyPropertyChanged
{
    public Guid Guid { get; set; }

    private string _displayName = "";
    private string _path = "";
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName != value)
            {
                _displayName = value;
                On();
            }
        }
    }
    public string Path
    {
        get => _path;
        set
        {
            if (_path != value)
            {
                _path = value;
                On();
            }
        }
    }

    private long _versionCount;
    private long _lastTicks;
    private long _size;
    private FileHealth _health = FileHealth.Watching;
    private bool _isActive = true;

    public long VersionCount
    {
        get => _versionCount;
        set
        {
            if (_versionCount != value)
            {
                _versionCount = value;
                On();
            }
        }
    }
    public long LastCapturedTicks
    {
        get => _lastTicks;
        set
        {
            if (_lastTicks != value)
            {
                _lastTicks = value;
                On(nameof(LastCapturedDisplay));
            }
        }
    }
    public long SizeBytes
    {
        get => _size;
        set
        {
            if (_size != value)
            {
                _size = value;
                On(nameof(SizeDisplay));
            }
        }
    }
    public FileHealth Health
    {
        get => _health;
        set
        {
            if (_health != value)
            {
                _health = value;
                On(nameof(HealthDisplay));
            }
        }
    }
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive != value)
            {
                _isActive = value;
                On(nameof(HealthDisplay));
            }
        }
    }

    internal long VersionCountField
    {
        get => _versionCount;
        set
        {
            _versionCount = value;
            On(nameof(VersionCount));
        }
    }
    internal long LastCapturedTicksField
    {
        get => _lastTicks;
        set
        {
            _lastTicks = value;
            On(nameof(LastCapturedDisplay));
        }
    }
    internal long SizeBytesField
    {
        get => _size;
        set
        {
            _size = value;
            On(nameof(SizeDisplay));
        }
    }
    internal FileHealth HealthField
    {
        get => _health;
        set
        {
            _health = value;
            On(nameof(HealthDisplay));
        }
    }
    internal bool IsActiveField
    {
        get => _isActive;
        set
        {
            _isActive = value;
            On(nameof(HealthDisplay));
        }
    }

    public string HealthDisplay =>
        !IsActive
            ? "已移除"
            : Health switch
            {
                FileHealth.Watching => "监听中",
                FileHealth.Syncing => "存版中",
                FileHealth.Degraded => "降级",
                FileHealth.Failing => "失败",
                FileHealth.Missing => "缺失",
                _ => Health.ToString(),
            };

    public string LastCapturedDisplay =>
        LastCapturedTicks == 0
            ? "—"
            : new DateTime(LastCapturedTicks, DateTimeKind.Utc)
                .ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");

    public string SizeDisplay => FormatBytes(SizeBytes);

    private static string FormatBytes(long n)
    {
        if (n < 1024)
            return n + " B";
        if (n < 1024L * 1024)
            return (n / 1024.0).ToString("F1") + " KB";
        if (n < 1024L * 1024 * 1024)
            return (n / 1024.0 / 1024).ToString("F1") + " MB";
        return (n / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void On([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
