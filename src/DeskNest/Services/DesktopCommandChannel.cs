using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskNest.Models;

namespace DeskNest.Services;

public sealed class DesktopCommandChannel : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly string name;
    private readonly Action<DesktopRequest> receive;
    private readonly Action<string> report;
    public Task Listening { get; }

    public DesktopCommandChannel(string name, Action<DesktopRequest> receive, Action<string> report)
    {
        this.name = name;
        this.receive = receive;
        this.report = report;
        Listening = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                // 当前用户专用管道；JSON 会转义文件名中的换行，完整传递多选列表。
                await using var pipe = new NamedPipeServerStream(name, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                var text = await reader.ReadLineAsync(timeout.Token);
                if (text == null || text.Length > 4_000_000) continue;
                var request = JsonSerializer.Deserialize<DesktopRequest>(text);
                if (request != null && request.Paths != null && request.Paths.Length <= 10_000 &&
                    request.Action is "show" or "new" or "quit" or "shell-enable" or "shell-disable") receive(request);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { break; }
            catch (Exception error) when (error is IOException or JsonException or OperationCanceledException)
            {
                report(error.Message);
            }
        }
    }

    public static async Task SendAsync(string name, DesktopRequest request)
    {
        await using var pipe = new NamedPipeClientStream(".", name, PipeDirection.Out, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(5000);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(JsonSerializer.Serialize(request));
    }

    public void Dispose() { cancellation.Cancel(); }
}
