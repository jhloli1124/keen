using Keen.Models;
using Keen.Services;
using Keen.Tray;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;

namespace Keen.Forms;

// 托盘优先:启动时不建主窗,只挂 NotifyIcon。
// 启动时武装被监控文件(WatchService)、开命名管道服务接收右键菜单的 --add;
// 退出走 async void、不阻塞 UI 线程,关停各后台件带超时,避免「退出变崩溃」。
internal sealed class TrayAppContext : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "Keen";

    private readonly ServiceProvider _services;
    private readonly NotifyIcon _tray;
    private readonly Form _invoker;
    private readonly SynchronizationContext _ui;
    private readonly Dictionary<Guid, FileHealth> _health = new();
    private readonly ToolStripMenuItem _autoItem;
    private readonly ToolStripMenuItem _shellItem;
    private readonly CancellationTokenSource _ipcCts = new();
    private Task? _ipcTask;
    private MainForm? _main; // 仅在 ShowMain 真正创建后赋值;退出时只动它
    private bool _exiting;
    private bool _disposed;

    public TrayAppContext(ServiceProvider services)
    {
        _services = services;
        _invoker = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            Size = new Size(0, 0),
        };
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();

        _autoItem = new ToolStripMenuItem("开机自启") { Checked = IsAutostartOn() };
        _shellItem = new ToolStripMenuItem("Explorer 右键：用 Keen 监控")
        {
            Checked = ShellIntegration.IsRegistered(),
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示主窗(&S)", image: null, (_, _) => ShowMain());
        menu.Items.Add(
            "设置(&E)…",
            image: null,
            (_, _) =>
            {
                using var f = new SettingsForm(_services);
                f.ShowDialog();
            }
        );
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_autoItem);
        menu.Items.Add(_shellItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出 Keen(&X)", image: null, (_, _) => ExitApp());

        _autoItem.Click += (_, _) =>
        {
            SetAutostart(!_autoItem.Checked);
            _autoItem.Checked = IsAutostartOn();
        };
        _shellItem.Click += (_, _) =>
        {
            try
            {
                if (_shellItem.Checked)
                    ShellIntegration.Unregister();
                else
                    ShellIntegration.Register();
                _shellItem.Checked = ShellIntegration.IsRegistered();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "切换右键菜单失败：" + ex.Message,
                    "Keen",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
            }
        };

        _tray = new NotifyIcon
        {
            Icon = TrayIcon.Idle,
            Text = "Keen（刻痕）",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowMain();
    }

    public async Task StartupAsync(string? addPath)
    {
        var pipeline = _services.GetRequiredService<BackupPipeline>();
        var watches = _services.GetRequiredService<WatchService>();
        pipeline.HealthChanged += OnHealth;
        pipeline.CaptureFailed += OnCaptureFailed;

        // IPC 服务先起:武装完成前右键 --add 才连得上管道,不至于被静默丢
        _ipcTask = Task.Run(() => IpcService.RunServerAsync(OnIpcPath, _ipcCts.Token));

        await watches.InitializeAsync();

        // 强制构造 RetentionService:DI 懒加载单例不解析就永不实例化,保留策略的定时器从未启动过
        _ = _services.GetRequiredService<RetentionService>();

        if (addPath is not null)
        {
            try
            {
                await watches.AddFileAsync(addPath);
            }
            catch { }
        }
    }

    private async Task OnIpcPath(string path)
    {
        var watches = _services.GetRequiredService<WatchService>();
        string? err = null;
        try
        {
            var wf = await watches.AddFileAsync(path);
            if (wf is null)
                err = "路径不支持、在保险库内或已存在";
        }
        catch (Exception ex)
        {
            err = ex.Message;
        }
        if (err is not null)
        {
            _ui.Post(
                _ =>
                {
                    try
                    {
                        _tray.ShowBalloonTip(
                            5000,
                            "Keen：添加监控失败",
                            $"{path}\n{err}",
                            ToolTipIcon.Warning
                        );
                    }
                    catch { }
                },
                null
            );
        }
    }

    private void OnHealth(Guid guid, FileHealth h, string? msg) =>
        _ui.Post(
            _ =>
            {
                _health[guid] = h;
                var anyBad = _health.Values.Any(x =>
                    x == FileHealth.Failing || x == FileHealth.Degraded || x == FileHealth.Missing
                );
                _tray.Icon = anyBad ? TrayIcon.Error : TrayIcon.Idle;
            },
            null
        );

    private void OnCaptureFailed(Guid guid, string name) =>
        _ui.Post(
            _ =>
            {
                try
                {
                    var cfg = _services.GetRequiredService<ConfigService>();
                    if (cfg.Current.NotifyOnFailure)
                        _tray.ShowBalloonTip(
                            5000,
                            "Keen：存版失败",
                            $"{name}\n捕获失败，将在稍后自动重试。",
                            ToolTipIcon.Warning
                        );
                }
                catch { }
            },
            null
        );

    private void ShowMain()
    {
        _main = _services.GetRequiredService<MainForm>();
        if (_main.WindowState == FormWindowState.Minimized)
            _main.WindowState = FormWindowState.Normal;
        if (!_main.Visible)
            _main.Show();
        _main.BringToFront();
        _main.Activate();
    }

    // async void:菜单 Click 回调,不阻塞 UI 线程。各步独立 try/catch,任何一处崩都不影响后续。
    private async void ExitApp()
    {
        if (_exiting)
            return;
        _exiting = true;

        try
        {
            _tray.Visible = false;
        }
        catch { }

        // 只在主窗确实被打开过时才关它;避免在关停期凭空构造一个 Form。
        try
        {
            if (_main is not null)
            {
                _main.AllowClose = true;
                _main.Close();
            }
        }
        catch { }

        // 关停后台件,带超时:卡住也最多等几秒就放行(进程反正要退)。
        try
        {
            await ShutdownAsync();
        }
        catch { }

        try
        {
            ExitThread();
        }
        catch { }
    }

    private async Task ShutdownAsync()
    {
        var pipeline = _services.GetRequiredService<BackupPipeline>();
        try
        {
            pipeline.HealthChanged -= OnHealth;
            pipeline.CaptureFailed -= OnCaptureFailed;
        }
        catch { }

        _ipcCts.Cancel();
        try
        {
            if (_ipcTask is not null)
                await Task.WhenAny(_ipcTask, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        catch { }

        try
        {
            var stop = pipeline.StopAsync();
            await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(3)));
        }
        catch { }

        // index / watcher / pipeline 的 Dispose 统一交给容器在 Dispose(disposing) 里做,避免双重释放。
    }

    private static bool IsAutostartOn()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(AppName) is string s && !string.IsNullOrEmpty(s);
    }

    private static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on)
            k.SetValue(AppName, Application.ExecutablePath);
        else
            k.DeleteValue(AppName, throwOnMissingValue: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed)
            return;
        _disposed = true;
        if (disposing)
        {
            try
            {
                _ipcCts.Dispose();
            }
            catch { }
            try
            {
                _tray.Dispose();
            }
            catch { }
            try
            {
                _invoker.Dispose();
            }
            catch { }
            try
            {
                _services.Dispose();
            }
            catch { }
        }
        base.Dispose(disposing);
    }
}
