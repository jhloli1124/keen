using Keen.Forms;
using Keen.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Keen;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var addPath = ParseAddPath(args);

        AppPaths.Ensure();
        ApplicationConfiguration.Initialize();
        // 跟随系统深/浅色主题(.NET 9+ WinForms 暗色模式);失败不阻塞启动。
        try
        {
            Application.SetColorMode(SystemColorMode.System);
        }
        catch { }

        using var single = new SingleInstance();
        if (!single.TryAcquire())
        {
            // 已有实例在跑:把 --add 的路径经命名管道转发给它,然后退出。
            if (addPath is not null)
            {
                try
                {
                    IpcService.SendPath(addPath);
                }
                catch { }
            }
            return;
        }

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogDir, "keen-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14
            )
            .CreateLogger();

        try
        {
            Log.Information("Keen 启动");
            var config = new ConfigService();
            config.LoadAsync().GetAwaiter().GetResult();
            Directory.CreateDirectory(config.Current.VaultRoot);

            var store = new VaultStore(config.Current.VaultRoot);
            store.SweepOrphans(); // 清上次崩溃在保险库残留的 .keenpartial

            var index = new VaultIndex(Path.Combine(config.Current.VaultRoot, "keen.sqlite"));

            using var services = BuildServices(config, store, index);
            using var ctx = new TrayAppContext(services);
            ctx.StartupAsync(addPath).GetAwaiter().GetResult();
            Application.Run(ctx);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Keen 未处理异常");
            throw;
        }
        finally
        {
            Log.Information("Keen 退出");
            Log.CloseAndFlush();
        }
    }

    // 形如:Keen.exe --add "C:\path\file.txt"
    private static string? ParseAddPath(string[] args)
    {
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], "--add", StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }

    private static ServiceProvider BuildServices(
        ConfigService config,
        VaultStore store,
        VaultIndex index
    )
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddSerilog(dispose: false));
        services.AddSingleton(config);
        services.AddSingleton(store);
        services.AddSingleton(index);
        services.AddSingleton<BackupPipeline>(sp => new BackupPipeline(
            sp.GetRequiredService<VaultStore>(),
            sp.GetRequiredService<VaultIndex>(),
            sp.GetRequiredService<ILogger<BackupPipeline>>()
        ));
        services.AddSingleton<FileWatchService>();
        services.AddSingleton<WatchService>();
        services.AddSingleton<RestoreService>();
        services.AddSingleton<RetentionService>();
        services.AddSingleton<MainForm>();
        return services.BuildServiceProvider();
    }
}
