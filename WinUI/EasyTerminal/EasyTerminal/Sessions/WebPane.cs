using EasyTerminal.Core.Models;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace EasyTerminal.Sessions;

/// <summary>A web page in the app: the AI assistant and the Proxmox console. Pop-ups go to the default browser.</summary>
public sealed class WebPane : Grid, IDisposable
{
    private readonly WebView2 _view = new();
    private readonly TextBlock _title = Ui.Secondary("");
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsOpen = false, IsClosable = false, Title = "无法加载网页内容" };
    private string _url;
    private bool _started;

    public WebPane(string url, Action? onClose = null)
    {
        _url = url;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var toolbar = onClose != null ? Ui.Columns("*", "auto", "auto", "auto") : Ui.Columns("*", "auto", "auto");
        toolbar.Padding = new Thickness(10, 4, 6, 4);
        _title.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_title.InColumn(0));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphRefresh, "刷新", Reload).InColumn(1));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphOpenExternal, "在浏览器中打开", () => _ = Windows.System.Launcher.LaunchUriAsync(new Uri(_view.Source?.ToString() ?? _url))).InColumn(2));
        if (onClose != null) toolbar.Children.Add(Ui.IconButton(Ui.GlyphClose, "关闭", onClose).InColumn(3));
        Children.Add(toolbar);
        SetRow(_error, 1);
        Children.Add(_error);
        SetRow(_view, 2);
        Children.Add(_view);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        try
        {
            // Must be chosen before the first WebView2 starts. Sign-ins live here, so they survive restarts.
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", Path.Combine(AppStore.DefaultDirectory, "webview"));
            await _view.EnsureCoreWebView2Async();
            var core = _view.CoreWebView2;
            core.NewWindowRequested += (sender, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var target) && target.Scheme is "https" or "http")
                    _ = Windows.System.Launcher.LaunchUriAsync(target);
            };
            core.DocumentTitleChanged += (_, _) => _title.Text = core.DocumentTitle;
            core.NavigationCompleted += (_, args) =>
            {
                _error.Message = args.IsSuccess ? "" : $"网页没有加载成功（{args.WebErrorStatus}）。可以点刷新重试，或在浏览器中打开。";
                _error.IsOpen = !args.IsSuccess;
            };
            core.Navigate(_url);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _error.Message = "需要 Microsoft Edge WebView2 运行库。" + ex.Message;
            _error.IsOpen = true;
        }
    }

    public void Navigate(string url)
    {
        _url = url;
        if (_view.CoreWebView2 != null) _view.CoreWebView2.Navigate(url);
    }

    public void Reload()
    {
        if (_view.CoreWebView2 != null) _view.CoreWebView2.Reload();
    }

#if DEBUG
    /// <summary>WebView2 draws outside XAML, so screenshots of the window can't see it; this asks it directly.</summary>
    internal async Task<string> SavePreviewAsync(string path)
    {
        var core = _view.CoreWebView2;
        if (core == null) return "not started: " + _error.Message;
        using (var file = File.Create(path))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file));
        return $"url={core.Source} title={core.DocumentTitle} error={_error.Message}";
    }
#endif

    public void Dispose() => _view.Close();
}
