using System.IO.Pipes;
using System.Text;

namespace Keen.Services;

// 单实例命名管道。Explorer 右键拉起的第二个实例(Keen.exe --add <path>)把路径转给已在跑的实例。
internal static class IpcService
{
    private const string PipeName = "Keen-v1";

    public static async Task RunServerAsync(Func<string, Task> onPath, CancellationToken ct)
    {
        using var server = new NamedPipeServerStream(
            PipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous
        );

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await server.WaitForConnectionAsync(ct);
                using var sr = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await sr.ReadLineAsync(ct);
                if (!string.IsNullOrWhiteSpace(line))
                    await onPath(line);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch when (!ct.IsCancellationRequested)
            { /* 单个连接出错不杀服务 */
            }
            try
            {
                server.Disconnect();
            }
            catch { }
        }
    }

    public static void SendPath(string path)
    {
        using var client = new NamedPipeClientStream(
            ".",
            PipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous
        );
        client.Connect(2000);
        using var sw = new StreamWriter(client, Encoding.UTF8) { AutoFlush = true };
        sw.WriteLine(path);
    }
}
