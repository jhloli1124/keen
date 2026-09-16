namespace Keen.Models;

// 薄引导配置;真正的权威是 SQLite 保险库索引(M2)。这里只存启动引导所需的最低信息。
internal sealed class AppConfig
{
    public string VaultRoot { get; set; } = "";
    public List<string> WatchedFiles { get; set; } = new();
    public bool Autostart { get; set; } = false;

    // 保留策略:0 = 不限
    public int RetainKeepLast { get; set; } = 0; // 每文件最多留最近 N 版
    public int RetainMaxAgeDays { get; set; } = 0; // 超过 X 天的清掉

    // 失败时弹 Windows 原生通知(toast)
    public bool NotifyOnFailure { get; set; } = true;

    // WinMerge 路径(#10 差异查看);null/空 = 自动探测常见安装位置
    public string? WinMergePath { get; set; }
}
