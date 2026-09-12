using System.ComponentModel;
using System.Diagnostics;
using Keen.Models;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keen.Forms;

internal sealed class HistoryForm : Form
{
    private readonly Guid _guid;
    private readonly string _displayName;
    private readonly IServiceProvider _sp;
    private readonly VaultIndex _index;
    private readonly VaultStore _store;
    private readonly RestoreService _restore;
    private readonly ConfigService _config;

    private readonly DataGridView _grid = new();
    private readonly BindingList<VersionRow> _rows = new();
    private readonly List<VersionEntry> _all = new();

    private readonly TextBox _filter = new() { Width = 220 };
    private readonly Button _open = new() { Text = "打开此版本" };
    private readonly Button _folder = new() { Text = "打开所在文件夹" };
    private readonly Button _restoreBtn = new() { Text = "恢复…" };
    private readonly Button _export = new() { Text = "导出另存…" };
    private readonly Button _noteBtn = new() { Text = "编辑备注" };
    private readonly Button _compareBtn = new() { Text = "对比(WinMerge)" };
    private readonly Button _refresh = new() { Text = "刷新" };

    public HistoryForm(Guid guid, string displayName, IServiceProvider sp)
    {
        _guid = guid; _displayName = displayName; _sp = sp;
        _index = sp.GetRequiredService<VaultIndex>();
        _store = sp.GetRequiredService<VaultStore>();
        _restore = sp.GetRequiredService<RestoreService>();
        _config = sp.GetRequiredService<ConfigService>();

        Text = $"Keen · 版本历史 — {displayName}";
        Width = 920; Height = 540;
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        Load += async (_, _) => await LoadAsync();
    }

    private void BuildUi()
    {
        var top = new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = $"  文件:{_displayName}",
            TextAlign = ContentAlignment.MiddleLeft,
        };

        var filterRow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            Padding = new Padding(8, 4, 8, 0),
            WrapContents = false,
        };
        filterRow.Controls.Add(new Label { Text = "筛选:", AutoSize = true, Margin = new Padding(0, 8, 6, 0) });
        filterRow.Controls.Add(_filter);
        // 说明:单选「对比」的基准取完整历史的上一版(语义正确),筛选时可能看不见——明说,免困惑
        filterRow.Controls.Add(new Label
        {
            Text = "单选「对比」的基准是完整历史里的上一版,可能被当前筛选隐藏",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(12, 8, 0, 0),
        });

        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 42,
            Padding = new Padding(8, 6, 8, 0),
            WrapContents = false,
        };
        foreach (var b in new[] { _open, _folder, _restoreBtn, _export, _noteBtn, _compareBtn, _refresh })
        {
            b.Margin = new Padding(0, 0, 8, 0);
            b.AutoSize = true;
            bar.Controls.Add(b);
        }

        _grid.Dock = DockStyle.Fill;
        _grid.AutoGenerateColumns = false;
        _grid.AllowUserToAddRows = false;
        _grid.ReadOnly = true;
        _grid.RowHeadersVisible = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.MultiSelect = true; // #10 对比需要选两个
        _grid.EnableHeadersVisualStyles = false; // #2 让表头跟随深/浅色
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(0, 90, 158);
        _grid.DefaultCellStyle.SelectionForeColor = Color.White;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        AddCol("时间", nameof(VersionRow.TimeDisplay), 160);
        AddCol("大小", nameof(VersionRow.SizeDisplay), 90);
        AddCol("类型", nameof(VersionRow.KindDisplay), 110);
        AddCol("较上一版", nameof(VersionRow.DeltaDisplay), 90);
        AddCol("备注", nameof(VersionRow.NoteDisplay), 220);
        _grid.DataSource = _rows;

        Controls.Add(_grid);
        Controls.Add(bar);
        Controls.Add(filterRow);
        Controls.Add(top);

        _filter.TextChanged += (_, _) => ApplyFilter();
        _open.Click += (_, _) => OpenVer();
        _folder.Click += (_, _) => OpenFolder();
        _restoreBtn.Click += async (_, _) => await RestoreAsync();
        _export.Click += (_, _) => Export();
        _noteBtn.Click += async (_, _) => await EditNoteAsync();
        _compareBtn.Click += (_, _) => Compare();
        _refresh.Click += async (_, _) => await LoadAsync();
    }

    private void AddCol(string header, string prop, int width) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            HeaderText = header,
            DataPropertyName = prop,
            Name = prop,
            Width = width,
            FillWeight = width,
        });

    private async Task LoadAsync()
    {
        _all.Clear();
        var vers = await _index.GetVersionsAsync(_guid);
        _all.AddRange(vers);
        ApplyFilter();
    }

    // #11 筛选:按备注 / 时间 / 类型文本包含(空=全部)。重建行后恢复原选中,
    // 否则 CurrentRow 重置到第 0 行,恢复/对比会作用在非用户所选的版本上。
    private void ApplyFilter()
    {
        var prevId = (_grid.CurrentRow?.DataBoundItem as VersionRow)?.Entry.Id ?? 0;
        var q = (_filter.Text ?? "").Trim();
        var deltaMap = ComputeDeltas();
        _rows.Clear();
        foreach (var v in _all)
        {
            if (q.Length > 0)
            {
                var row = new VersionRow { Entry = v, DeltaBytes = deltaMap.GetValueOrDefault(v.Id) };
                if (!(row.NoteDisplay?.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                      || row.TimeDisplay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                      || row.KindDisplay.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
            }
            _rows.Add(new VersionRow { Entry = v, DeltaBytes = deltaMap.GetValueOrDefault(v.Id) });
        }
        if (prevId != 0)
        {
            for (int i = 0; i < _grid.Rows.Count; i++)
            {
                if (_grid.Rows[i].DataBoundItem is VersionRow vr && vr.Entry.Id == prevId)
                {
                    _grid.ClearSelection();
                    _grid.Rows[i].Selected = true;
                    _grid.CurrentCell = _grid.Rows[i].Cells[0];
                    break;
                }
            }
        }
    }

    private Dictionary<long, long> ComputeDeltas()
    {
        var asc = _all.OrderBy(v => v.CapturedAtTicks).ThenBy(v => v.Seq).ToList();
        var map = new Dictionary<long, long>();
        long prev = -1;
        foreach (var v in asc)
        {
            map[v.Id] = prev < 0 ? 0 : v.SizeBytes - prev;
            prev = v.SizeBytes;
        }
        return map;
    }

    private VersionEntry? SelectedOne() => (_grid.CurrentRow?.DataBoundItem as VersionRow)?.Entry;

    private async Task EditNoteAsync()
    {
        if (SelectedOne() is not { } e) return;
        using var dlg = new InputDialog("编辑备注", "给这个版本加个备注:", e.Note);
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        await _index.SetVersionNoteAsync(e.Id, string.IsNullOrWhiteSpace(dlg.Value) ? null : dlg.Value);
        e.Note = dlg.Value;
        ApplyFilter();
    }

    private void OpenVer()
    {
        if (SelectedOne() is not { } e) return;
        try { Process.Start(new ProcessStartInfo(_store.FullPath(e.StoredRelPath)) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开失败:" + ex.Message, "Keen"); }
    }

    private void OpenFolder()
    {
        if (SelectedOne() is not { } e) return;
        var path = _store.FullPath(e.StoredRelPath);
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, "打开失败:" + ex.Message, "Keen"); }
    }

    private async Task RestoreAsync()
    {
        if (SelectedOne() is not { } e) return;
        var when = new DateTime(e.CapturedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

        // 与 RestoreService 同样的目标路径解析:优先被监控文件的当前路径
        var wf = await _index.GetWatchedFileAsync(e.WatchedGuid);
        var target = wf is not null && !string.IsNullOrEmpty(wf.CurrentPath) ? wf.CurrentPath : e.OrigPathAtCapture;
        var exists = File.Exists(target);

        var ok = MessageBox.Show(this,
            exists
                ? $"恢复到所选版本?\n\n版本:{when}(大小 {VersionRow.FormatBytes(e.SizeBytes)})\n\n当前的原文件会先自动存进历史(作为「恢复前快照」),\n所以这一步可以再撤销。\n\n确定恢复?"
                : $"原文件已不存在(被删除或移动)。\n恢复将按所选版本新建还原文件。\n\n版本:{when}(大小 {VersionRow.FormatBytes(e.SizeBytes)})\n\n确定恢复?",
            "Keen · 恢复", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (ok != DialogResult.OK) return;

        UseWaitCursor = true;
        _restoreBtn.Enabled = false;
        try
        {
            await _restore.RestoreAsync(e);
            MessageBox.Show(this,
                exists
                    ? "已恢复。原文件已回到所选版本;恢复前的状态已存为「恢复前快照」。"
                    : "源文件被删除,已成功新建还原文件。",
                "Keen", MessageBoxButtons.OK, MessageBoxIcon.Information);
            await LoadAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "恢复失败:" + ex.Message, "Keen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        finally
        {
            _restoreBtn.Enabled = true;
            UseWaitCursor = false;
        }
    }

    private void Export()
    {
        if (SelectedOne() is not { } e) return;
        using var sfd = new SaveFileDialog { FileName = e.OrigFilename, Title = "导出此版本到…" };
        if (sfd.ShowDialog(this) != DialogResult.OK) return;
        try { File.Copy(_store.FullPath(e.StoredRelPath), sfd.FileName, overwrite: true); }
        catch (Exception ex) { MessageBox.Show(this, "导出失败:" + ex.Message, "Keen"); }
    }

    private void Compare()
    {
        var selected = new List<VersionEntry>();
        foreach (DataGridViewRow r in _grid.SelectedRows)
            if (r.DataBoundItem is VersionRow vr) selected.Add(vr.Entry);

        if (selected.Count == 0)
        {
            MessageBox.Show(this, "选中要对比的版本(1~3 个)。\n选 1 个 = 和上一版对比。", "Keen");
            return;
        }

        List<string> files;
        if (selected.Count == 1)
        {
            // 和上一版(更旧的那一版)对比
            var idx = _all.FindIndex(v => v.Id == selected[0].Id);
            if (idx < 0 || idx + 1 >= _all.Count)
            {
                MessageBox.Show(this, "这已经是最早的版本,没有更早的可对比。", "Keen");
                return;
            }
            var older = _all[idx + 1]; // _all 是 DESC(新→旧),idx+1 是更旧
            files = new() { _store.FullPath(older.StoredRelPath), _store.FullPath(selected[0].StoredRelPath) };
        }
        else if (selected.Count <= 3)
        {
            files = selected
                .OrderBy(v => v.CapturedAtTicks).ThenBy(v => v.Seq)
                .Select(v => _store.FullPath(v.StoredRelPath))
                .ToList();
        }
        else
        {
            MessageBox.Show(this, "最多选三个版本(三方对比)。", "Keen");
            return;
        }

        var exe = WinMergeHelper.Find(_config.Current.WinMergePath);
        if (exe is null)
        {
            MessageBox.Show(this, "未找到 WinMerge。\n可在设置里指定 WinMergeU.exe 路径(留空则自动探测)。",
                "Keen", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try { WinMergeHelper.Compare(exe, files); }
        catch (Exception ex) { MessageBox.Show(this, "启动 WinMerge 失败:" + ex.Message, "Keen"); }
    }
}

internal sealed class VersionRow
{
    public VersionEntry Entry { get; set; } = new();
    public long DeltaBytes { get; set; }

    public string TimeDisplay =>
        new DateTime(Entry.CapturedAtTicks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    public string SizeDisplay => FormatBytes(Entry.SizeBytes);
    public string KindDisplay => Entry.Kind switch
    {
        VersionKind.Normal => "常规",
        VersionKind.PreRestoreSnapshot => "恢复前快照",
        VersionKind.SyntheticRestore => "恢复",
        _ => Entry.Kind.ToString(),
    };
    public string DeltaDisplay => DeltaBytes == 0 ? "—" :
        (DeltaBytes > 0 ? "+" : "-") + FormatBytes(Math.Abs(DeltaBytes));
    public string NoteDisplay => Entry.Note ?? "";

    internal static string FormatBytes(long n)
    {
        if (n < 1024) return n + " B";
        if (n < 1024L * 1024) return (n / 1024.0).ToString("F1") + " KB";
        if (n < 1024L * 1024 * 1024) return (n / 1024.0 / 1024).ToString("F1") + " MB";
        return (n / 1024.0 / 1024 / 1024).ToString("F2") + " GB";
    }
}
