using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace SwayWin;

internal sealed class CommandServer : IDisposable
{
    private readonly CancellationTokenSource cancellation = new();
    private readonly Func<string, Task<string>> handle;
    private readonly Action? afterReply;
    private readonly string serverPipeName;
    private readonly Task server;
    internal static string PipeName => $"swaywin-{Environment.UserName}-{Process.GetCurrentProcess().SessionId}";
    internal static string MutexName => $"Local\\{PipeName}";

    internal CommandServer(Func<string, Task<string>> handle, Action? afterReply = null, string? pipeName = null)
    {
        this.handle = handle;
        this.afterReply = afterReply;
        serverPipeName = pipeName ?? PipeName;
        server = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                using NamedPipeServerStream pipe = new(serverPipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellation.Token);
                using StreamReader reader = new(pipe, Encoding.UTF8, false, 1024, true);
                using StreamWriter writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? request = await ReadBoundedLine(reader, timeout.Token);
                if (request is null) continue;
                string response = await handle(request).WaitAsync(timeout.Token);
                try { await writer.WriteLineAsync(response.AsMemory(), timeout.Token); }
                finally { afterReply?.Invoke(); }
            }
            catch (OperationCanceledException) { }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { AppLog.Write($"IPC: {exception.Message}"); }
        }
    }

    internal static async Task<string> Send(string message, string? pipeName = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
        using NamedPipeClientStream pipe = new(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        using StreamWriter writer = new(pipe, new UTF8Encoding(false), 1024, true) { AutoFlush = true };
        using StreamReader reader = new(pipe, Encoding.UTF8, false, 1024, true);
        await writer.WriteLineAsync(message.AsMemory(), timeout.Token);
        return await reader.ReadLineAsync(timeout.Token) ?? JsonSerializer.Serialize(new[] { new CommandResult(false, "Manager closed the connection.") });
    }

    private static async Task<string?> ReadBoundedLine(StreamReader reader, CancellationToken token)
    {
        StringBuilder line = new();
        char[] character = new char[1];
        while (line.Length < 65536)
        {
            int read = await reader.ReadAsync(character.AsMemory(), token);
            if (read == 0) return line.Length == 0 ? null : line.ToString();
            if (character[0] == '\n') return line.ToString().TrimEnd('\r');
            line.Append(character[0]);
        }
        throw new IOException("IPC command exceeds 64 KiB.");
    }

    public void Dispose()
    {
        cancellation.Cancel();
        _ = server.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
    }
}

internal sealed record CommandResult(bool success, string? error = null);
