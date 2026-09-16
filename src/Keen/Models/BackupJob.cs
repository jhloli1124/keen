namespace Keen.Models;

internal sealed class BackupJob
{
    public Guid WatchedGuid { get; init; }
    public string SourcePath { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public VersionKind Kind { get; init; } = VersionKind.Normal;

    // PreRestoreSnapshot 等必须留痕的作业绕过去重(即便字节与上次相同)。
    public bool BypassDedupe { get; init; }

    // 非空时,消费完成后置结果;供 CaptureDirectAsync 同步等待。
    public TaskCompletionSource<VersionEntry?>? Tcs { get; init; }
}
