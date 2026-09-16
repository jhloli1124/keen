using Microsoft.Win32;

namespace Keen.Services;

// Explorer 右键动词(per-user、HKCU、免管理员、可逆)。
// 注册到 *\shell\Keen.Monitor —— 对所有文件生效;命令:Keen.exe --add "%1"。
// 注意:verb 里固化了 Keen.exe 的当前路径,移动 publish 目录后需重新开关一次。
internal static class ShellIntegration
{
    private const string VerbKey = @"Software\Classes\*\shell\Keen.Monitor";

    public static bool IsRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(VerbKey);
        return k is not null;
    }

    public static void Register()
    {
        var exe = Application.ExecutablePath;
        using var verb = Registry.CurrentUser.CreateSubKey(VerbKey);
        verb.SetValue(null, "用 Keen 监控");
        verb.SetValue("Icon", exe + ",0");
        using var cmd = verb.CreateSubKey("command");
        cmd.SetValue(null, $"\"{exe}\" --add \"%1\"");
    }

    public static void Unregister()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(VerbKey, throwOnMissingSubKey: false);
        }
        catch { }
    }
}
