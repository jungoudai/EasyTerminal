using EasyTerminal.Core.Ssh;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Services;

/// <summary>ContentDialogs must not overlap, so every dialog in the app goes through one gate.</summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static XamlRoot? Root { get; set; }

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        await Gate.WaitAsync();
        try
        {
            dialog.XamlRoot = Root;
            dialog.Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
            Sessions.RdpPane.Suppress(true);
            try
            {
                return await dialog.ShowAsync();
            }
            finally
            {
                Sessions.RdpPane.Suppress(false);
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task<string?> PromptTextAsync(string title, string header, string initial = "", string confirmText = "确定", string placeholder = "")
    {
        var box = new TextBox { Header = header, Text = initial, PlaceholderText = placeholder, MinWidth = 380 };
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = confirmText, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(string title, string message, string confirmText = "确定", bool destructive = false)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = confirmText,
            CloseButtonText = "取消",
            DefaultButton = destructive ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        return await ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public static Task AlertAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer { Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true }, MaxHeight = 320 },
            CloseButtonText = "关闭",
        };
        return ShowAsync(dialog);
    }

    public static async Task<string?> PromptSecretAsync(string title, string message)
    {
        var box = new PasswordBox { PlaceholderText = "在此输入" };
        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog { Title = title, Content = panel, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.Opened += (_, _) => box.Focus(FocusState.Programmatic);
        return await ShowAsync(dialog) == ContentDialogResult.Primary ? box.Password : null;
    }

    /// <summary>Answers a prompt from ssh, ssh-keygen or their askpass helper.</summary>
    public static async Task<string?> AnswerSshPromptAsync(AskPassRequest request, Action<string> notify)
    {
        string prompt = request.Prompt;
        if (request.Kind == AskPassKind.Notify)
        {
            notify(prompt);
            return "";
        }
        if (request.Kind == AskPassKind.Confirm)
            return await ConfirmAsync("OpenSSH 确认", prompt, "允许") ? "yes" : null;

        if (prompt.Contains("(yes/no", StringComparison.OrdinalIgnoreCase))
        {
            // A first-seen host key: the fingerprint in the message is what the user has to compare.
            bool trust = await ConfirmAsync("确认主机身份", prompt, "信任并继续");
            return trust ? "yes" : "no";
        }
        return await PromptSecretAsync("OpenSSH 需要输入", prompt);
    }
}
