using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using EasyTerminal.Core.Models;

namespace EasyTerminal.Core.Ssh;

/// <summary>A background <c>ssh -N</c> that holds port forwards open. Dispose ends it.</summary>
public sealed class SshTunnel : IDisposable
{
    private readonly Process _process;
    private readonly SshContext _context;
    private readonly System.Text.StringBuilder _errors = new();
    private int _disposed;

    /// <summary>Raised on a background thread when ssh ends by itself, with what it reported.</summary>
    public event Action<string>? Exited;

    public bool IsAlive => !_process.HasExited;

    private SshTunnel(Process process, SshContext context)
    {
        _process = process;
        _context = context;
        process.EnableRaisingEvents = true;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) lock (_errors) _errors.AppendLine(e.Data);
        };
        process.Exited += (_, _) =>
        {
            if (Volatile.Read(ref _disposed) == 0) Exited?.Invoke(ErrorText);
        };
        process.BeginErrorReadLine();
        process.StandardInput.Close();
    }

    public string ErrorText
    {
        get
        {
            lock (_errors) return _errors.ToString().Trim();
        }
    }

    public static async Task<SshTunnel> OpenAsync(SshRunner runner, Device device, Connection? connection, IReadOnlyList<string> forwardArguments, CancellationToken cancellation = default)
    {
        var context = await runner.PrepareAsync(device, connection, false, null, cancellation);
        var arguments = new List<string> { "-N", "-T", "-o", "ExitOnForwardFailure=yes" };
        arguments.AddRange(forwardArguments);
        arguments.Add(context.Alias);
        return new SshTunnel(runner.StartBackground(context, arguments.ToArray()), context);
    }

    /// <summary>Waits until a local port accepts connections, failing early when ssh gives up.</summary>
    public async Task WaitForLocalPortAsync(int port, TimeSpan timeout, CancellationToken cancellation = default)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (_process.HasExited) throw new IOException(ErrorText.Length > 0 ? ErrorText : "SSH 隧道意外结束");
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port, cancellation);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(200, cancellation);
            }
        }
        throw new TimeoutException("等待 SSH 隧道就绪超时");
    }

    public static int FindFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            _process.Kill(true);
        }
        catch (InvalidOperationException)
        {
        }
        _process.Dispose();
        _context.Dispose();
    }
}

public static class PortForwardArguments
{
    public static IReadOnlyList<string> For(PortForward rule) => rule.Kind switch
    {
        PortForwardKind.Local => new[] { "-L", $"{Bind(rule)}:{Host(rule.TargetHost)}:{rule.TargetPort}" },
        PortForwardKind.Remote => new[] { "-R", $"{Bind(rule)}:{Host(rule.TargetHost)}:{rule.TargetPort}" },
        _ => new[] { "-D", Bind(rule) },
    };

    private static string Bind(PortForward rule) => $"{Host(rule.BindAddress)}:{rule.BindPort}";

    // ssh separates fields with ':', so IPv6 literals need brackets.
    private static string Host(string host) => host.Contains(':') && !host.StartsWith('[') ? $"[{host}]" : host;

    public static string Describe(PortForward rule) => rule.Kind switch
    {
        PortForwardKind.Local => $"本地 {Bind(rule)} → 远程 {Host(rule.TargetHost)}:{rule.TargetPort}",
        PortForwardKind.Remote => $"远程 {Bind(rule)} → 本地 {Host(rule.TargetHost)}:{rule.TargetPort}",
        _ => $"SOCKS5 代理 {Bind(rule)}",
    };

    public static string? Validate(PortForward rule)
    {
        if (rule.BindPort is < 1 or > 65535) return "请填写有效的监听端口。";
        if (string.IsNullOrWhiteSpace(rule.BindAddress) || rule.BindAddress.Contains(' ')) return "监听地址无效。";
        if (rule.Kind == PortForwardKind.Dynamic) return null;
        if (string.IsNullOrWhiteSpace(rule.TargetHost) || rule.TargetHost.Contains(' ')) return "目标地址无效。";
        return rule.TargetPort is < 1 or > 65535 ? "请填写有效的目标端口。" : null;
    }
}
