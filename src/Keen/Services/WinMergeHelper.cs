using System.Diagnostics;
using Microsoft.Win32;

namespace Keen.Services;

// #10 差异查看:外部调用 WinMerge(不自己实现 diff)。
// 查找顺序:用户在设置里指定的路径 → 注册表 Uninstall 项 → PATH → 常见安装路径(兜底)。
internal static class WinMergeHelper
{
    private static readonly string[] FallbackPaths =
    {
        @"C:\Program Files\WinMerge\WinMergeU.exe",
        @"C:\Program Files (x86)\WinMerge\WinMergeU.exe",
    };

    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var fromReg = FindViaRegistry();
        if (fromReg is not null)
            return fromReg;

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (
            var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        )
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), "WinMergeU.exe");
                if (File.Exists(full))
                    return full;
            }
            catch { }
        }

        foreach (var p in FallbackPaths)
            if (File.Exists(p))
                return p;
        return null;
    }

    private static string? FindViaRegistry()
    {
        try
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
                );
                if (uninstall is null)
                    continue;

                string[] subNames;
                try
                {
                    subNames = uninstall.GetSubKeyNames();
                }
                catch
                {
                    continue;
                } // 某些 hive 无权限

                foreach (var subName in subNames)
                {
                    try
                    {
                        using var sk = uninstall.OpenSubKey(subName);
                        if (sk is null)
                            continue;

                        var name = sk.GetValue("DisplayName") as string;
                        if (
                            name is null
                            || name.IndexOf("WinMerge", StringComparison.OrdinalIgnoreCase) < 0
                        )
                            continue;

                        var hit = ResolveExe(sk);
                        if (hit is not null)
                            return hit;
                    }
                    catch
                    { /* 单个子键无权限/损坏,跳过 */
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string? ResolveExe(RegistryKey sk)
    {
        // Inno Setup 通常写 InstallLocation。
        if (sk.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
        {
            var exe = Path.Combine(loc.Trim().Trim('"'), "WinMergeU.exe");
            if (File.Exists(exe))
                return exe;
        }

        // DisplayIcon 形如 "C:\...\WinMerge.exe,0" 或 "C:\...\WinMergeU.exe"。
        if (sk.GetValue("DisplayIcon") is string icon && !string.IsNullOrWhiteSpace(icon))
        {
            var p = icon.Split(',')[0].Trim().Trim('"');
            if (p.EndsWith("WinMergeU.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p))
                return p;
            if (File.Exists(p))
            {
                var u = Path.Combine(Path.GetDirectoryName(p) ?? "", "WinMergeU.exe");
                if (File.Exists(u))
                    return u;
            }
        }

        // UninstallString 形如 "\"C:\...\unins000.exe\"" —— "unins" 之前的段就是安装目录本身。
        if (sk.GetValue("UninstallString") is string un && !string.IsNullOrWhiteSpace(un))
        {
            var p = un.Trim().Trim('"');
            var idx = p.IndexOf("unins", StringComparison.OrdinalIgnoreCase);
            if (idx > 0)
            {
                var dir = p[..idx].TrimEnd('\\', '"', ' ');
                if (!string.IsNullOrEmpty(dir))
                {
                    var exe = Path.Combine(dir, "WinMergeU.exe");
                    if (File.Exists(exe))
                        return exe;
                }
            }
        }

        return null;
    }

    public static void Compare(string exe, IReadOnlyList<string> files)
    {
        var args = string.Join(" ", files.Select(f => $"\"{f}\""));
        Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }
}
