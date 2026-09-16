using Keen.Platform;

namespace Keen.Services;

internal sealed class StaleHandleException : Exception
{
    public StaleHandleException(string path)
        : base($"stale file handle (atomic rename-over detected): {path}") { }
}

// 唯一对源文件做「复制用打开」的地方(不变量①)。
// 每次都开全新句柄,并用 FileId 比对确认它仍指向当前路径的文件;一旦编辑器原子改名把旧句柄
// 剥离到旧 inode(会静默复制到保存前的字节),就抛 StaleHandleException 让调用方重试。
// 调用方拿到流后必须立即读取,不得跨 Task.Delay 持有。
internal static class SourceFile
{
    private const int Buf = 1 << 20; // 1 MiB

    public static FileStream OpenVerified(string path)
    {
        var s1 = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            Buf,
            FileOptions.Asynchronous | FileOptions.SequentialScan
        );

        try
        {
            var id1 = NativeMethods.GetFileIdOrThrow(s1.SafeFileHandle);

            using var s2 = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                Buf,
                FileOptions.Asynchronous | FileOptions.SequentialScan
            );
            var id2 = NativeMethods.GetFileIdOrThrow(s2.SafeFileHandle);

            if (id1.Equals(id2))
                return s1;
            s1.Dispose();
            throw new StaleHandleException(path);
        }
        catch (StaleHandleException)
        {
            throw;
        }
        catch
        {
            s1.Dispose();
            throw;
        }
    }
}
