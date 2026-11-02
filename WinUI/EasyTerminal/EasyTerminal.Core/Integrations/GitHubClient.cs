using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using EasyTerminal.Core.Ssh;

namespace EasyTerminal.Core.Integrations;

public sealed record GitHubKey(long Id, string Title, string Key);

/// <summary>The part of the GitHub REST API that manages a user's SSH keys.</summary>
public sealed class GitHubClient
{
    public const string TokenPageUrl = "https://github.com/settings/tokens/new?scopes=admin:public_key&description=EasyTerminal";

    private readonly HttpClient _http;
    private readonly string _token;

    public GitHubClient(string token, HttpMessageHandler? handler = null)
    {
        _token = token;
        _http = handler != null ? new HttpClient(handler, false) : new HttpClient();
        _http.BaseAddress = new Uri("https://api.github.com/");
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("EasyTerminal");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<string> GetLoginAsync(CancellationToken cancellation = default)
    {
        using var document = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "user", null, cancellation));
        return document.RootElement.GetProperty("login").GetString() ?? "";
    }

    public async Task<IReadOnlyList<GitHubKey>> ListKeysAsync(CancellationToken cancellation = default)
    {
        using var document = JsonDocument.Parse(await SendAsync(HttpMethod.Get, "user/keys?per_page=100", null, cancellation));
        return document.RootElement.EnumerateArray()
            .Select(k => new GitHubKey(k.GetProperty("id").GetInt64(), k.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "", k.GetProperty("key").GetString() ?? ""))
            .ToList();
    }

    public async Task<GitHubKey> AddKeyAsync(string title, string publicKey, CancellationToken cancellation = default)
    {
        string body = JsonSerializer.Serialize(new Dictionary<string, string> { ["title"] = title, ["key"] = publicKey.Trim() });
        using var document = JsonDocument.Parse(await SendAsync(HttpMethod.Post, "user/keys", body, cancellation));
        var root = document.RootElement;
        return new GitHubKey(root.GetProperty("id").GetInt64(), root.GetProperty("title").GetString() ?? title, root.GetProperty("key").GetString() ?? publicKey);
    }

    public Task DeleteKeyAsync(long id, CancellationToken cancellation = default) =>
        SendAsync(HttpMethod.Delete, "user/keys/" + id, null, cancellation);

    /// <summary>Compares two public keys by type and key data; GitHub drops the comment when it stores one.</summary>
    public static bool SameKey(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return a.Length >= 2 && b.Length >= 2 && a[0] == b[0] && a[1] == b[1];
    }

    /// <summary>The token of a signed-in GitHub CLI, or null when the CLI is missing or signed out.</summary>
    public static async Task<string?> TryGetCliTokenAsync()
    {
        try
        {
            var result = await ProcessRunner.RunAsync("gh", new[] { "auth", "token" }, TimeSpan.FromSeconds(10));
            string token = result.Output.Trim();
            return result.ExitCode == 0 && token.Length > 0 ? token : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or TimeoutException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<string> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellation)
    {
        using var request = new HttpRequestMessage(method, path);
        // Per request rather than as a default header, so the token can't follow a redirect by accident.
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        if (body != null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellation);
        string text = await response.Content.ReadAsStringAsync(cancellation);
        if (response.IsSuccessStatusCode) return text;
        throw new IOException(Describe(response.StatusCode, text));
    }

    private static string Describe(HttpStatusCode status, string body)
    {
        string message = "";
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.TryGetProperty("message", out var m)) message = m.GetString() ?? "";
            if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                foreach (var error in errors.EnumerateArray())
                    if (error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var detail))
                        message += " " + detail.GetString();
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            HttpStatusCode.Unauthorized => "GitHub 令牌无效或已过期，请重新连接。",
            // GitHub answers 404 rather than 403 when a token lacks the scope.
            HttpStatusCode.NotFound or HttpStatusCode.Forbidden =>
                "这个令牌没有管理 SSH 密钥的权限。经典令牌需要 admin:public_key；细粒度令牌需要 “Git SSH keys” 读写权限；gh 登录需先运行：gh auth refresh -h github.com -s admin:public_key",
            HttpStatusCode.UnprocessableEntity when message.Contains("already in use", StringComparison.OrdinalIgnoreCase) =>
                "这把公钥已经绑定在某个 GitHub 账号上。GitHub 不允许同一把公钥绑定两个账号。",
            _ => $"GitHub 返回 {(int)status}：{message}".TrimEnd(),
        };
    }
}
