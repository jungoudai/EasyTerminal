using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

namespace EasyTerminal.Core.Ssh;

/// <summary>
/// Entry points used when ssh.exe re-invokes this executable. The app ships as a single exe, so these
/// run before any UI initialization and talk to ssh over the inherited stdio.
/// </summary>
public static class SshHelperEntry
{
    public const string AskPassPipeVariable = "EASYTERMINAL_ASKPASS_PIPE";
    public const string AskPassTokenVariable = "EASYTERMINAL_ASKPASS_TOKEN";

    public static string ProxyPasswordVariable(string host, int port) =>
        "EASYTERMINAL_PROXY_" + Convert.ToHexString(Encoding.UTF8.GetBytes($"{host}:{port}"));

    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args[0] == "--proxy-connect")
        {
            exitCode = RunProxyConnect(args);
            return true;
        }

        // ssh runs SSH_ASKPASS with the prompt as its only argument and cannot pass flags,
        // so helper mode is signalled by the pipe variable being present.
        string? pipe = Environment.GetEnvironmentVariable(AskPassPipeVariable);
        if (!string.IsNullOrEmpty(pipe))
        {
            exitCode = RunAskPass(pipe, args[0]);
            return true;
        }
        return false;
    }

    private static int RunAskPass(string pipeName, string prompt)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
            pipe.Connect(5000);
            string kind = Environment.GetEnvironmentVariable("SSH_ASKPASS_PROMPT") ?? "";
            using var writer = new BinaryWriter(pipe, Encoding.UTF8, true);
            using var reader = new BinaryReader(pipe, Encoding.UTF8, true);
            writer.Write(Environment.GetEnvironmentVariable(AskPassTokenVariable) ?? "");
            writer.Write(kind);
            writer.Write(prompt);
            writer.Flush();
            if (!reader.ReadBoolean()) return 1;
            string answer = reader.ReadString();
            using var stdout = Console.OpenStandardOutput();
            stdout.Write(Encoding.UTF8.GetBytes(answer + "\n"));
            return 0;
        }
        catch (Exception)
        {
            return 1;
        }
    }

    private static int RunProxyConnect(string[] args)
    {
        // --proxy-connect <kind> <proxyHost> <proxyPort> <targetHost> <targetPort> [user]
        if (args.Length < 6) return 2;
        try
        {
            string kind = args[1];
            string proxyHost = args[2];
            int proxyPort = int.Parse(args[3]);
            string targetHost = args[4];
            int targetPort = int.Parse(args[5]);
            string? user = args.Length > 6 ? args[6] : null;
            string? password = Environment.GetEnvironmentVariable(ProxyPasswordVariable(proxyHost, proxyPort));

            using var client = new TcpClient { NoDelay = true };
            client.Connect(proxyHost, proxyPort);
            var stream = client.GetStream();
            ProxyHandshake.Perform(stream, kind, targetHost, targetPort, user, password);

            using var stdin = Console.OpenStandardInput();
            using var stdout = Console.OpenStandardOutput();
            var up = Task.Run(() =>
            {
                try
                {
                    stdin.CopyTo(stream);
                }
                catch (IOException)
                {
                }
                try
                {
                    client.Client.Shutdown(SocketShutdown.Send);
                }
                catch (SocketException)
                {
                }
            });
            var down = Task.Run(() =>
            {
                try
                {
                    var buffer = new byte[32 * 1024];
                    int read;
                    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        stdout.Write(buffer, 0, read);
                        stdout.Flush();
                    }
                }
                catch (IOException)
                {
                }
            });
            // ssh closes its pipes when finished; the server closing the socket also ends the session.
            Task.WaitAny(up, down);
            return 0;
        }
        catch (Exception ex)
        {
            // ssh shows this to the user; a raw UTF-8 write keeps it readable whatever the console codepage is.
            using var stderr = Console.OpenStandardError();
            stderr.Write(Encoding.UTF8.GetBytes("代理连接失败: " + ex.Message + "\n"));
            return 1;
        }
    }
}

/// <summary>Client side of the SOCKS4a, SOCKS5 and HTTP CONNECT handshakes.</summary>
public static class ProxyHandshake
{
    public static void Perform(Stream stream, string kind, string host, int port, string? user, string? password)
    {
        switch (kind.ToLowerInvariant())
        {
            case "socks5": Socks5(stream, host, port, user, password); break;
            case "socks4": Socks4(stream, host, port, user); break;
            case "http": HttpConnect(stream, host, port, user, password); break;
            default: throw new ArgumentException("未知代理类型: " + kind);
        }
    }

    private static void Socks5(Stream stream, string host, int port, string? user, string? password)
    {
        bool hasAuth = !string.IsNullOrEmpty(user);
        stream.Write(hasAuth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 });
        var reply = ReadExact(stream, 2);
        if (reply[0] != 5) throw new IOException("代理不是 SOCKS5 服务器");
        if (reply[1] == 2)
        {
            if (!hasAuth) throw new IOException("SOCKS5 代理要求用户名和密码");
            byte[] u = Encoding.UTF8.GetBytes(user!);
            byte[] p = Encoding.UTF8.GetBytes(password ?? "");
            var auth = new List<byte> { 1, (byte)u.Length };
            auth.AddRange(u);
            auth.Add((byte)p.Length);
            auth.AddRange(p);
            stream.Write(auth.ToArray());
            var authReply = ReadExact(stream, 2);
            if (authReply[1] != 0) throw new IOException("SOCKS5 认证失败");
        }
        else if (reply[1] != 0)
        {
            throw new IOException("SOCKS5 代理不接受任何认证方式");
        }

        byte[] name = Encoding.ASCII.GetBytes(host);
        if (name.Length > 255) throw new IOException("主机名过长");
        var request = new List<byte> { 5, 1, 0, 3, (byte)name.Length };
        request.AddRange(name);
        request.Add((byte)(port >> 8));
        request.Add((byte)port);
        stream.Write(request.ToArray());

        var head = ReadExact(stream, 4);
        if (head[1] != 0) throw new IOException($"SOCKS5 连接被拒绝 (代码 {head[1]})");
        int skip = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => ReadExact(stream, 1)[0],
            _ => throw new IOException("SOCKS5 返回了未知地址类型"),
        };
        ReadExact(stream, skip + 2);
    }

    private static void Socks4(Stream stream, string host, int port, string? user)
    {
        // SOCKS4a: IP 0.0.0.x tells the proxy to resolve the trailing hostname.
        var request = new List<byte> { 4, 1, (byte)(port >> 8), (byte)port, 0, 0, 0, 1 };
        if (!string.IsNullOrEmpty(user)) request.AddRange(Encoding.ASCII.GetBytes(user));
        request.Add(0);
        request.AddRange(Encoding.ASCII.GetBytes(host));
        request.Add(0);
        stream.Write(request.ToArray());
        var reply = ReadExact(stream, 8);
        if (reply[1] != 90) throw new IOException($"SOCKS4 连接被拒绝 (代码 {reply[1]})");
    }

    private static void HttpConnect(Stream stream, string host, int port, string? user, string? password)
    {
        var request = new StringBuilder();
        request.Append($"CONNECT {host}:{port} HTTP/1.1\r\nHost: {host}:{port}\r\n");
        if (!string.IsNullOrEmpty(user))
        {
            string token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
            request.Append($"Proxy-Authorization: Basic {token}\r\n");
        }
        request.Append("\r\n");
        stream.Write(Encoding.ASCII.GetBytes(request.ToString()));

        // Read byte by byte so nothing from the tunnelled connection is consumed.
        var header = new StringBuilder();
        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            int b = stream.ReadByte();
            if (b < 0) throw new IOException("HTTP 代理提前断开");
            header.Append((char)b);
            if (header.Length > 65536) throw new IOException("HTTP 代理响应过长");
        }
        string statusLine = header.ToString().Split("\r\n")[0];
        var parts = statusLine.Split(' ');
        if (parts.Length < 2 || parts[1] != "200") throw new IOException("HTTP 代理拒绝连接: " + statusLine);
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        var buffer = new byte[count];
        stream.ReadExactly(buffer);
        return buffer;
    }
}
