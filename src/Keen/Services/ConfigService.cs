using System.Text.Json;
using Keen.Models;

namespace Keen.Services;

internal sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly SemaphoreSlim _lock = new(1, 1);

    public AppConfig Current { get; private set; } = new();

    public async Task LoadAsync()
    {
        await _lock.WaitAsync();
        try
        {
            if (!File.Exists(AppPaths.ConfigFile))
            {
                Current = new AppConfig();
                NormalizeVault();
                return;
            }

            try
            {
                var json = await File.ReadAllTextAsync(AppPaths.ConfigFile);
                Current = JsonSerializer.Deserialize<AppConfig>(json) ?? new AppConfig();
            }
            catch (Exception ex)
            {
                // 损坏的 config 不能阻塞启动,但也不能无声重置(会把迁移过的保险库指回默认空库)。
                // 改名保留现场 + 记错误日志,再落默认。
                try
                {
                    File.Move(
                        AppPaths.ConfigFile,
                        AppPaths.ConfigFile + ".corrupt",
                        overwrite: true
                    );
                }
                catch { }
                Serilog.Log.Error(
                    ex,
                    "config.json 损坏,已保留为 config.json.corrupt 并使用默认配置"
                );
                Current = new AppConfig();
            }

            NormalizeVault();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SaveAsync()
    {
        await _lock.WaitAsync();
        try
        {
            var dir = Path.GetDirectoryName(AppPaths.ConfigFile);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var tmp = AppPaths.ConfigFile + ".tmp";
            var json = JsonSerializer.Serialize(Current, JsonOpts);
            await File.WriteAllTextAsync(tmp, json);

            // 同卷原子替换:File.Replace 要求目标已存在,首写时用 File.Move。
            if (File.Exists(AppPaths.ConfigFile))
                File.Replace(tmp, AppPaths.ConfigFile, destinationBackupFileName: null);
            else
                File.Move(tmp, AppPaths.ConfigFile);
        }
        finally
        {
            _lock.Release();
        }
    }

    private void NormalizeVault()
    {
        if (string.IsNullOrWhiteSpace(Current.VaultRoot))
            Current.VaultRoot = AppPaths.Vault;
    }
}
