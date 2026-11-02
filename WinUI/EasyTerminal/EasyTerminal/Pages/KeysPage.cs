using EasyTerminal.Core.Integrations;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace EasyTerminal.Pages;

/// <summary>Keys in ~/.ssh, including FIDO2 keys generated on a hardware security key.</summary>
public sealed class KeysPage : UserControl, IRefreshablePage
{
    private readonly AppServices _app = AppServices.Current;
    private readonly StackPanel _list = new() { Spacing = 8 };
    private readonly StackPanel _otherGitHubKeys = new() { Spacing = 8 };
    private readonly StackPanel _knownHosts = new() { Spacing = 8 };
    private readonly Border _github = Ui.Card(new Grid());
    private IReadOnlyList<SshKeyInfo> _keys = Array.Empty<SshKeyInfo>();
    private IReadOnlyList<GitHubKey> _githubKeys = Array.Empty<GitHubKey>();

    public KeysPage()
    {
        var (root, content) = Ui.Page();
        Content = root;
        var header = Ui.Columns("*", "auto");
        header.Children.Add(Ui.Title("密钥").InColumn(0));
        header.Children.Add(Ui.Button("生成密钥", Ui.GlyphAdd, () => _ = GenerateAsync(), accent: true).InColumn(1));
        content.Children.Add(header);
        content.Children.Add(Ui.Secondary($"位置：{KeyManager.DefaultDirectory}。私钥文件只会留在这台电脑上；硬件密钥的私钥始终保存在安全密钥内。", true));
        content.Children.Add(_github);
        content.Children.Add(_list);
        content.Children.Add(_otherGitHubKeys);
        var knownHosts = new Expander { Header = "已知主机（known_hosts）", HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Content = _knownHosts, Margin = new Thickness(0, 12, 0, 0) };
        knownHosts.Expanding += (_, _) => RefreshKnownHosts();
        content.Children.Add(knownHosts);
    }

    public async void Refresh()
    {
        _keys = await KeyManager.ListAsync();
        await RefreshGitHubAsync();
    }

    private void RenderKeys()
    {
        _list.Children.Clear();
        if (_keys.Count == 0) _list.Children.Add(Ui.Secondary("还没有 SSH 密钥。"));
        foreach (var key in _keys) _list.Children.Add(BuildRow(key));

        _otherGitHubKeys.Children.Clear();
        var others = _githubKeys.Where(remote => !_keys.Any(local => GitHubClient.SameKey(local.PublicKey, remote.Key))).ToList();
        if (others.Count == 0) return;
        _otherGitHubKeys.Children.Add(Ui.Section("GitHub 上的其他密钥"));
        foreach (var remote in others)
        {
            var row = Ui.Columns("auto", "*", "auto");
            row.Children.Add(Ui.Icon(Ui.GlyphGlobe, 16).InColumn(0));
            var text = new StackPanel();
            text.Children.Add(Ui.Body(remote.Title));
            text.Children.Add(Ui.Secondary(KnownHostsFile.Fingerprint(remote.Key.Split(' ').ElementAtOrDefault(1) ?? "")));
            row.Children.Add(text.InColumn(1));
            row.Children.Add(Ui.IconButton(Ui.GlyphDelete, "从 GitHub 移除", () => _ = RemoveFromGitHubAsync(remote)).InColumn(2));
            _otherGitHubKeys.Children.Add(Ui.Card(row, 10));
        }
    }

    // ---- GitHub ------------------------------------------------------------------------------

    private GitHubClient? GitHub => _app.Store.Secrets.Get(_app.Store.Data.GitHubTokenSecretId) is { } token ? new GitHubClient(token) : null;

    private async Task RefreshGitHubAsync()
    {
        string? problem = null;
        _githubKeys = Array.Empty<GitHubKey>();
        if (GitHub is { } client)
        {
            try
            {
                _githubKeys = await Task.Run(() => client.ListKeysAsync());
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                problem = ex.Message;
            }
        }
        RenderGitHubCard(problem);
        RenderKeys();
    }

    private void RenderGitHubCard(string? problem)
    {
        var row = Ui.Columns("auto", "*", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphGlobe, 18).InColumn(0));
        var text = new StackPanel();
        string login = _app.Store.Data.GitHubLogin;
        bool connected = _app.Store.Data.GitHubTokenSecretId != null;
        text.Children.Add(Ui.Strong(connected ? "GitHub：" + login : "GitHub"));
        text.Children.Add(Ui.Secondary(problem ?? (connected ? $"账号上有 {_githubKeys.Count} 把 SSH 密钥。点密钥右侧的上传按钮即可添加。" : "连接后可以一键把公钥添加到 GitHub 的 SSH Keys。"), true));
        row.Children.Add(text.InColumn(1));
        row.Children.Add((connected
            ? Ui.Button("断开", Ui.GlyphClose, () => _ = DisconnectGitHubAsync())
            : Ui.Button("连接 GitHub", Ui.GlyphAdd, () => _ = ConnectGitHubAsync(), accent: true)).InColumn(2));
        _github.Child = row;
    }

    private async Task<bool> ConnectGitHubAsync()
    {
        var token = new PasswordBox { Header = "令牌", PlaceholderText = "ghp_… 或 github_pat_…" };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsOpen = false };
        var useCli = new Button { Content = "使用 GitHub CLI 的登录" };
        useCli.Click += async (_, _) =>
        {
            string? found = await GitHubClient.TryGetCliTokenAsync();
            if (found != null) token.Password = found;
            else
            {
                error.Message = "没有找到已登录的 GitHub CLI（gh）。";
                error.IsOpen = true;
            }
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Ui.Button("在 GitHub 创建令牌", Ui.GlyphOpenExternal, () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(GitHubClient.TokenPageUrl))));
        buttons.Children.Add(useCli);

        var panel = new StackPanel { Spacing = 10, MinWidth = 440 };
        panel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "需要一个有 admin:public_key 权限的令牌。下面的按钮会打开 GitHub 并预先选好权限，生成后粘贴到这里。令牌用 Windows 数据保护加密保存，只发送给 api.github.com。" });
        panel.Children.Add(buttons);
        panel.Children.Add(token);
        panel.Children.Add(error);
        var dialog = new ContentDialog { Title = "连接 GitHub", Content = panel, PrimaryButtonText = "连接", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var deferral = args.GetDeferral();
            try
            {
                string value = token.Password.Trim();
                string login = await Task.Run(() => new GitHubClient(value).GetLoginAsync());
                _app.Store.Data.GitHubTokenSecretId = _app.Store.Secrets.Set(_app.Store.Data.GitHubTokenSecretId, value);
                _app.Store.Data.GitHubLogin = login;
                _app.Store.Save();
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or FormatException or System.Text.Json.JsonException or InvalidOperationException)
            {
                error.Message = ex.Message;
                error.IsOpen = true;
                args.Cancel = true;
            }
            deferral.Complete();
        };
        bool connected = await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
        if (connected) await RefreshGitHubAsync();
        return connected;
    }

    private async Task DisconnectGitHubAsync()
    {
        if (!await Dialogs.ConfirmAsync("断开 GitHub？", "只会删除这台电脑上保存的令牌。GitHub 上的密钥不受影响，令牌本身可以在 GitHub 设置中撤销。", "断开", destructive: true)) return;
        _app.Store.Secrets.Remove(_app.Store.Data.GitHubTokenSecretId);
        _app.Store.Data.GitHubTokenSecretId = null;
        _app.Store.Data.GitHubLogin = "";
        _app.Store.Save();
        await RefreshGitHubAsync();
    }

    private async Task UploadToGitHubAsync(SshKeyInfo key)
    {
        if (GitHub == null && !await ConnectGitHubAsync()) return;
        if (_githubKeys.Any(remote => GitHubClient.SameKey(key.PublicKey, remote.Key))) return;
        try
        {
            string title = $"EasyTerminal {Environment.MachineName} {Path.GetFileName(key.Path)}";
            await Task.Run(() => GitHub!.AddKeyAsync(title, key.PublicKey));
            await RefreshGitHubAsync();
            App.MainWindowInstance?.ShowNotice("已添加到 GitHub", $"{Path.GetFileName(key.Path)} 已添加到 {_app.Store.Data.GitHubLogin} 的 SSH Keys。", InfoBarSeverity.Success);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            await Dialogs.AlertAsync("无法上传到 GitHub", ex.Message);
        }
    }

    private async Task RemoveFromGitHubAsync(GitHubKey remote)
    {
        if (!await Dialogs.ConfirmAsync($"从 GitHub 移除 {remote.Title}？", "使用这把密钥的设备将无法再访问 GitHub。", "移除", destructive: true)) return;
        try
        {
            await Task.Run(() => GitHub!.DeleteKeyAsync(remote.Id));
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or TaskCanceledException)
        {
            await Dialogs.AlertAsync("无法移除", ex.Message);
        }
        await RefreshGitHubAsync();
    }

    // ---- Known hosts -------------------------------------------------------------------------

    private void RefreshKnownHosts()
    {
        _knownHosts.Children.Clear();
        IReadOnlyList<KnownHost> entries;
        try
        {
            entries = KnownHostsFile.Read();
        }
        catch (IOException ex)
        {
            _knownHosts.Children.Add(Ui.Secondary(ex.Message, true));
            return;
        }
        if (entries.Count == 0) _knownHosts.Children.Add(Ui.Secondary("还没有已知主机。"));
        foreach (var entry in entries)
        {
            var row = Ui.Columns("*", "auto");
            var text = new StackPanel();
            text.Children.Add(Ui.Body(entry.IsHashed ? "（已哈希的主机名）" : entry.Hosts));
            text.Children.Add(Ui.Secondary($"{entry.KeyType} · {entry.Fingerprint}"));
            row.Children.Add(text.InColumn(0));
            row.Children.Add(Ui.IconButton(Ui.GlyphDelete, "移除", () => _ = RemoveKnownHostAsync(entry)).InColumn(1));
            _knownHosts.Children.Add(row);
        }
    }

    private async Task RemoveKnownHostAsync(KnownHost entry)
    {
        if (!await Dialogs.ConfirmAsync("移除这条记录？", $"{entry.Hosts}\n{entry.Fingerprint}\n\n下次连接时需要重新确认主机身份。只在你确认主机确实更换过密钥时才这样做。", "移除", destructive: true)) return;
        try
        {
            if (!KnownHostsFile.Remove(entry)) await Dialogs.AlertAsync("文件已变化", "known_hosts 已被修改，请重新确认要移除的记录。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.AlertAsync("无法移除", ex.Message);
        }
        RefreshKnownHosts();
    }

    private FrameworkElement BuildRow(SshKeyInfo key)
    {
        var row = Ui.Columns("auto", "*", "auto", "auto", "auto", "auto");
        row.Children.Add(Ui.Icon(Ui.GlyphKey, 18).InColumn(0));
        var text = new StackPanel();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Ui.Body(Path.GetFileName(key.Path)));
        if (key.IsHardwareBacked)
            title.Children.Add(new Border { Child = new TextBlock { Text = "硬件密钥", FontSize = 11 }, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), Background = Ui.Brush("AccentFillColorDefaultBrush"), RequestedTheme = ElementTheme.Dark });
        var uploaded = _githubKeys.FirstOrDefault(remote => GitHubClient.SameKey(key.PublicKey, remote.Key));
        if (uploaded != null)
            title.Children.Add(new Border { Child = new TextBlock { Text = "已在 GitHub", FontSize = 11 }, Padding = new Thickness(6, 1, 6, 1), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Ui.Brush("CardStrokeColorDefaultBrush") });
        text.Children.Add(title);
        text.Children.Add(Ui.Secondary($"{key.Type} · {key.Fingerprint}" + (key.Comment.Length > 0 ? " · " + key.Comment : "")));
        row.Children.Add(text.InColumn(1));
        row.Children.Add(Ui.IconButton(Ui.GlyphCopy, "复制公钥", () =>
        {
            var package = new DataPackage();
            package.SetText(key.PublicKey);
            Clipboard.SetContent(package);
        }).InColumn(2));
        row.Children.Add((uploaded != null
            ? Ui.IconButton(Ui.GlyphAccept, "已在 GitHub，点击移除", () => _ = RemoveFromGitHubAsync(uploaded))
            : Ui.IconButton(Ui.GlyphGlobe, "上传到 GitHub", () => _ = UploadToGitHubAsync(key))).InColumn(3));
        row.Children.Add(Ui.IconButton(Ui.GlyphUpload, "部署到设备", () => _ = DeployAsync(key)).InColumn(4));
        row.Children.Add(Ui.IconButton(Ui.GlyphDelete, "删除", () => _ = DeleteAsync(key)).InColumn(5));
        return Ui.Card(row, 10);
    }

    private async Task DeleteAsync(SshKeyInfo key)
    {
        string warning = key.IsHardwareBacked
            ? "只会删除本机的公钥和密钥句柄文件。如果是可发现凭据（resident），安全密钥内的凭据不受影响。"
            : "私钥文件会被永久删除，已部署此公钥的主机将无法再用它登录。";
        if (!await Dialogs.ConfirmAsync($"删除 {Path.GetFileName(key.Path)}？", warning, "删除", destructive: true)) return;
        try
        {
            File.Delete(key.Path);
            File.Delete(key.Path + ".pub");
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync("无法删除", ex.Message);
        }
        Refresh();
    }

    private async Task DeployAsync(SshKeyInfo key)
    {
        var picker = new ComboBox { Header = "部署到", HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 340 };
        foreach (var device in _app.Store.Data.Devices)
            picker.Items.Add(new ComboBoxItem { Content = $"{device.Name} ({device.Host})", Tag = device });
        if (picker.Items.Count == 0)
        {
            await Dialogs.AlertAsync("没有设备", "请先添加一台设备。");
            return;
        }
        picker.SelectedIndex = 0;
        var dialog = new ContentDialog { Title = "部署公钥", Content = picker, PrimaryButtonText = "部署", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var target = (Device)((ComboBoxItem)picker.SelectedItem).Tag;
        try
        {
            var result = await Task.Run(() => _app.Ssh.RunRemoteAsync(target, null, KeyManager.DeployScript(key.PublicKey), TimeSpan.FromMinutes(2)));
            if (result.ExitCode != 0) throw new IOException(result.Error.Trim());
            await Dialogs.AlertAsync("部署完成", $"公钥已写入 {target.Name} 的 authorized_keys。");
        }
        catch (Exception ex)
        {
            await Dialogs.AlertAsync("部署失败", ex.Message);
        }
    }

    public async Task GenerateAsync()
    {
        var type = new ComboBox { Header = "类型", HorizontalAlignment = HorizontalAlignment.Stretch };
        type.Items.Add(new ComboBoxItem { Content = "Ed25519-SK（硬件安全密钥，推荐）", Tag = KeyType.Ed25519Sk });
        type.Items.Add(new ComboBoxItem { Content = "ECDSA-SK（硬件安全密钥，兼容旧设备）", Tag = KeyType.EcdsaSk });
        type.Items.Add(new ComboBoxItem { Content = "Ed25519（软件密钥）", Tag = KeyType.Ed25519 });
        type.Items.Add(new ComboBoxItem { Content = "RSA 4096（软件密钥）", Tag = KeyType.Rsa4096 });
        type.Items.Add(new ComboBoxItem { Content = "ECDSA P-521（软件密钥）", Tag = KeyType.Ecdsa });
        type.SelectedIndex = 0;

        var fileName = new TextBox { Header = "文件名", Text = NextFileName("id_ed25519_sk") };
        var comment = new TextBox { Header = "注释", Text = $"{Environment.UserName}@{Environment.MachineName}" };
        var passphrase = new PasswordBox { Header = "密码短语（可选）" };
        var resident = new CheckBox { Content = "可发现凭据（resident）：把凭据存进安全密钥，可在其他电脑上恢复" };
        var verify = new CheckBox { Content = "每次使用都要求 PIN 或生物识别（verify-required）" };
        var application = new TextBox { Header = "应用标识（可选）", PlaceholderText = "ssh:work，用于在一把密钥上保存多个凭据" };
        var hardwareOptions = new StackPanel { Spacing = 8 };
        hardwareOptions.Children.Add(resident);
        hardwareOptions.Children.Add(verify);
        hardwareOptions.Children.Add(application);

        type.SelectionChanged += (_, _) =>
        {
            var selected = (KeyType)((ComboBoxItem)type.SelectedItem).Tag;
            hardwareOptions.Visibility = KeyManager.IsHardwareType(selected) ? Visibility.Visible : Visibility.Collapsed;
            string stem = selected switch
            {
                KeyType.Ed25519Sk => "id_ed25519_sk",
                KeyType.EcdsaSk => "id_ecdsa_sk",
                KeyType.Ed25519 => "id_ed25519",
                KeyType.Rsa4096 => "id_rsa",
                _ => "id_ecdsa",
            };
            fileName.Text = NextFileName(stem);
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        panel.Children.Add(type);
        panel.Children.Add(fileName);
        panel.Children.Add(comment);
        panel.Children.Add(passphrase);
        panel.Children.Add(hardwareOptions);
        var dialog = new ContentDialog
        {
            Title = "生成 SSH 密钥",
            Content = new ScrollViewer { Content = panel, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "生成",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var options = new KeyGenerationOptions
        {
            Type = (KeyType)((ComboBoxItem)type.SelectedItem).Tag,
            FileName = fileName.Text.Trim(),
            Comment = comment.Text.Trim(),
            Passphrase = passphrase.Password,
            Resident = resident.IsChecked == true,
            VerifyRequired = verify.IsChecked == true,
            Application = application.Text.Trim(),
        };
        await RunGenerationAsync(options);
    }

    private static string NextFileName(string stem)
    {
        string candidate = stem;
        for (int i = 2; File.Exists(Path.Combine(KeyManager.DefaultDirectory, candidate)) || File.Exists(Path.Combine(KeyManager.DefaultDirectory, candidate + ".pub")); i++)
            candidate = $"{stem}_{i}";
        return candidate;
    }

    private async Task RunGenerationAsync(KeyGenerationOptions options)
    {
        var overlay = new ContentDialog
        {
            Title = KeyManager.IsHardwareType(options.Type) ? "请操作你的安全密钥" : "正在生成密钥",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new ProgressBar { IsIndeterminate = true },
                    new TextBlock
                    {
                        TextWrapping = TextWrapping.Wrap,
                        Text = KeyManager.IsHardwareType(options.Type)
                            ? "请插入安全密钥。Windows 弹出安全提示后，按提示输入 PIN 并触摸密钥。"
                            : "请稍候…",
                    },
                },
            },
            CloseButtonText = "取消",
            XamlRoot = Dialogs.Root,
        };

        using var cancel = new CancellationTokenSource();
        overlay.CloseButtonClick += (_, _) => cancel.Cancel();
        // Windows Hello draws its own UI above this one, so the overlay only needs to explain the wait.
        var shown = overlay.ShowAsync();
        try
        {
            string token = Guid.NewGuid().ToString("N");
            var key = await KeyGenerationRunner.RunAsync(options, _app, token, cancel.Token);
            overlay.Hide();
            await shown;

            var package = new DataPackage();
            package.SetText(key.PublicKey);
            Clipboard.SetContent(package);
            await Dialogs.AlertAsync("密钥已生成", $"{key.Path}\n\n{key.Fingerprint}\n\n公钥已复制到剪贴板。");
        }
        catch (OperationCanceledException)
        {
            overlay.Hide();
        }
        catch (Exception ex)
        {
            overlay.Hide();
            try
            {
                await shown;
            }
            catch (Exception)
            {
                // The overlay may already be gone.
            }
            await Dialogs.AlertAsync("生成失败", ex.Message);
        }
        Refresh();
    }
}

internal static class KeyGenerationRunner
{
    public static Task<SshKeyInfo> RunAsync(KeyGenerationOptions options, AppServices app, string token, CancellationToken cancellation) =>
        Task.Run(() => app.Ssh.GenerateKeyAsync(options, token, cancellation), cancellation);
}
