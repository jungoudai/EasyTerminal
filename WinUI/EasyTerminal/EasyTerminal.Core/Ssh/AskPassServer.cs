using System.IO.Pipes;
using System.Text;

namespace EasyTerminal.Core.Ssh;

public enum AskPassKind { Secret, Confirm, Notify }

public sealed record AskPassRequest(string Token, AskPassKind Kind, string Prompt);

/// <summary>
/// One-per-process pipe that ssh's askpass helper connects to. The pipe name is random and the default
/// ACL limits it to this user, so saved passwords only travel to children this app launched.
/// </summary>
public sealed class AskPassServer : IDisposable
{
    private readonly Func<AskPassRequest, Task<string?>> _handler;
    private readonly CancellationTokenSource _stop = new();

    public string PipeName { get; } = "easyterminal-" + Guid.NewGuid().ToString("N");

    public AskPassServer(Func<AskPassRequest, Task<string?>> handler)
    {
        _handler = handler;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync();
                return;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
                using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
                string token = reader.ReadString();
                string kindText = reader.ReadString();
                string prompt = reader.ReadString();
                var kind = kindText switch
                {
                    "confirm" => AskPassKind.Confirm,
                    "none" => AskPassKind.Notify,
                    _ => AskPassKind.Secret,
                };
                string? answer = await _handler(new AskPassRequest(token, kind, prompt.Trim()));
                writer.Write(answer != null);
                if (answer != null) writer.Write(answer);
                writer.Flush();
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
