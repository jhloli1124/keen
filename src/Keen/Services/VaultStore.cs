using System.Buffers;
using System.Security.Cryptography;

namespace Keen.Services;

// blob I/O(不变量④):写到 .keenpartial 用 WriteThrough + Flush(flushToDisk) + 长度校验,
// 再同卷原子 File.Move。复制过程中流式计算 SHA-256(供去重/基线/恢复校验)。
internal sealed class VaultStore
{
    private readonly string _root;

    public VaultStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<(long size, string sha256)> WriteBlobAsync(
        Stream source,
        string relPath,
        IProgress<long>? progress,
        CancellationToken ct
    )
    {
        var full = Path.Combine(_root, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + ".keenpartial";

        long total;
        byte[] hash;
        await using (
            var dst = new FileStream(
                tmp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1 << 20,
                FileOptions.Asynchronous | FileOptions.SequentialScan | FileOptions.WriteThrough
            )
        )
        using (var sha = SHA256.Create())
        {
            var buf = ArrayPool<byte>.Shared.Rent(1 << 20);
            try
            {
                total = 0;
                long lastReport = 0;
                int n;
                while ((n = await source.ReadAsync(buf.AsMemory(0, buf.Length), ct)) > 0)
                {
                    sha.TransformBlock(buf, 0, n, null, 0);
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    total += n;
                    if (progress is not null && total - lastReport >= 1 << 20)
                    {
                        lastReport = total;
                        progress.Report(total);
                    }
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                hash = sha.Hash ?? throw new IOException("sha256 未产生结果");
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }
            await dst.FlushAsync(ct);
            dst.Flush(flushToDisk: true);
            if (dst.Length != total)
                throw new IOException(
                    $"length mismatch writing {relPath}: {dst.Length} != {total}"
                );
        }

        if (File.Exists(full))
            File.Delete(full);
        File.Move(tmp, full);
        progress?.Report(total);

        return (total, BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant());
    }

    // relPath 在库里以 `/` 分隔存储(guid/YYYY-MM/文件);explorer /select 认不了混合斜杠路径,统一规范化成 `\`。
    public string FullPath(string relPath) => Path.GetFullPath(Path.Combine(_root, relPath));

    public void DeleteBlob(string relPath)
    {
        try
        {
            var full = Path.Combine(_root, relPath);
            if (File.Exists(full))
                File.Delete(full);
        }
        catch
        { /* 删除失败不影响主流程 */
        }
    }

    // 启动清扫:保险库里残留的 *.keenpartial(上次崩溃在写入中途留下的)。
    public void SweepOrphans()
    {
        try
        {
            foreach (
                var f in Directory.EnumerateFiles(
                    _root,
                    "*.keenpartial",
                    SearchOption.AllDirectories
                )
            )
            {
                try
                {
                    File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
}
