using System.Runtime.InteropServices;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Rdp;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Sessions;

/// <summary>
/// A remote desktop embedded in a tab through the system's RDP ActiveX control. The control is a child
/// window drawn above XAML, so it is moved to follow its slot and hidden while anything must appear on top.
/// </summary>
public sealed class RdpPane : Grid, IDisposable
{
    private const uint ChildStyle = 0x40000000 | 0x04000000 | 0x02000000; // WS_CHILD | WS_CLIPSIBLINGS | WS_CLIPCHILDREN
    private const int ConnectedDisconnected = 0;
    private const int MinimumDesktopSize = 200, MaximumDesktopSize = 8192;
    private static readonly Guid NonScriptableId = new("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E");
    private static readonly nint TopWindow = 0;
    private static int _suppressed;
    private static readonly List<RdpPane> Open = new();

    [DllImport("atl.dll")]
    private static extern int AtlAxWinInit();

    [DllImport("atl.dll")]
    private static extern int AtlAxGetControl(nint window, out nint unknown);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    private readonly Device _device;
    private readonly Connection _connection;
    private readonly AppServices _app = AppServices.Current;
    private readonly Grid _surface = new();
    private readonly StackPanel _overlay = new() { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, MaxWidth = 520 };
    private readonly TextBlock _overlayText = new() { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly ProgressRing _ring = new() { IsActive = false, Width = 32, Height = 32 };
    private readonly TextBlock _status = Ui.Secondary("");
    private readonly Button _disconnect;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _poll;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _resize;
    private nint _window;
    private ComDispatch? _control;
    private SshTunnel? _tunnel;
    private int _attempt;
    private int _lastState = ConnectedDisconnected;
    private bool _active;
    private bool _connecting;
    private bool _everConnected;
    private (int X, int Y, int Width, int Height) _lastRect;
    private (int Width, int Height) _desktopSize;

    public bool IsConnected => _connecting || _lastState != ConnectedDisconnected;
    public event Action? StateChanged;

    /// <summary>Hides every remote desktop while a dialog or popup needs to appear over it.</summary>
    public static void Suppress(bool on)
    {
        _suppressed = Math.Max(0, _suppressed + (on ? 1 : -1));
        foreach (var pane in Open) pane.UpdateWindowVisibility();
    }

    public RdpPane(Device device, Connection connection)
    {
        _device = device;
        _connection = connection;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _disconnect = Ui.Button("断开", Ui.GlyphStop, Disconnect);
        var toolbar = Ui.Columns("auto", "*", "auto", "auto", "auto");
        toolbar.Padding = new Thickness(12, 6, 12, 6);
        toolbar.Children.Add(Ui.Icon(Ui.GlyphRemoteDesktop, 14).InColumn(0));
        _status.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_status.InColumn(1));
        toolbar.Children.Add(_disconnect.InColumn(2));
        toolbar.Children.Add(Ui.Button("重新连接", Ui.GlyphRefresh, Reconnect).InColumn(3));
        toolbar.Children.Add(Ui.Button("用系统远程桌面打开", Ui.GlyphOpenExternal, () => _ = OpenExternallyAsync()).InColumn(4));
        Children.Add(toolbar);

        _overlay.Children.Add(_ring);
        _overlay.Children.Add(_overlayText);
        _surface.Children.Add(_overlay);
        SetRow(_surface, 1);
        Children.Add(_surface);

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(500);
        _poll.Tick += (_, _) => PollState();
        _resize = DispatcherQueue.CreateTimer();
        _resize.IsRepeating = false;
        _resize.Interval = TimeSpan.FromMilliseconds(600);
        _resize.Tick += (_, _) => ApplyDesktopSize();

        LayoutUpdated += (_, _) => SyncWindowRect();
        Open.Add(this);
    }

    public void SetActive(bool active)
    {
        _active = active;
        Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        UpdateWindowVisibility();
        SyncWindowRect();
    }

    private void UpdateWindowVisibility()
    {
        if (_window == 0) return;
        bool show = _active && _suppressed == 0 && _lastState != ConnectedDisconnected;
        ShowWindow(_window, show ? 5 : 0); // SW_SHOW : SW_HIDE
    }

    // ---- Connecting --------------------------------------------------------------------------

    public void Reconnect() => _ = ConnectAsync();

    private void ShowOverlay(string message, bool busy)
    {
        _overlayText.Text = message;
        _ring.IsActive = busy;
        _ring.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _overlay.Visibility = Visibility.Visible;
    }

    private async Task ConnectAsync()
    {
        int attempt = ++_attempt;
        TearDownConnection();
        _connecting = true;
        _everConnected = false;
        _status.Text = "正在连接";
        ShowOverlay("正在连接…", true);
        StateChanged?.Invoke();
        try
        {
            var route = await _app.OpenRdpRouteAsync(_device, _connection);
            if (attempt != _attempt)
            {
                route.Tunnel?.Dispose();
                return;
            }
            _tunnel = route.Tunnel;
            if (_tunnel != null)
                _tunnel.Exited += message => DispatcherQueue.TryEnqueue(() =>
                {
                    if (attempt == _attempt) Fail("SSH 隧道已断开。" + message);
                });

            EnsureControl();
            Configure(route.Host, route.Port);
            _control!.Call("Connect");
            _status.Text = route.Description;
            _lastState = 2;
            _poll.Start();
            UpdateWindowVisibility();
            SyncWindowRect();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (attempt == _attempt) Fail(ex.Message);
        }
    }

    private void Fail(string message)
    {
        TearDownConnection();
        _status.Text = "未连接";
        ShowOverlay(message + "\n\n可以点上方“重新连接”，或改用系统远程桌面打开。", false);
        StateChanged?.Invoke();
    }

    private void EnsureControl()
    {
        if (_window != 0) return;
        AtlAxWinInit();
        // Newest first: the version-independent name is the usual answer, but not every Windows build registers it.
        foreach (var progId in new[] { "MsTscAx.MsTscAx", "MsTscAx.MsTscAx.13", "MsTscAx.MsTscAx.12", "MsTscAx.MsTscAx.11", "MsTscAx.MsTscAx.10", "MsTscAx.MsTscAx.9" })
        {
            nint window = CreateWindowExW(0, "AtlAxWin", progId, ChildStyle, 0, 0, 1, 1, MainWindow.WindowHandle, 0, 0, 0);
            if (window == 0) continue;
            if (AtlAxGetControl(window, out nint unknown) >= 0 && unknown != 0)
            {
                try
                {
                    _control = ComDispatch.FromUnknown(unknown);
                    _nonScriptable = unknown;
                    Marshal.AddRef(unknown);
                    _window = window;
                    return;
                }
                finally
                {
                    Marshal.Release(unknown);
                }
            }
            DestroyWindow(window);
        }
        throw new InvalidOperationException("这台电脑上无法加载系统的远程桌面控件（mstscax.dll）。");
    }

    private nint _nonScriptable;

    private (int Width, int Height) MeasureDesktop()
    {
        double scale = XamlRoot?.RasterizationScale ?? 1;
        // Some servers reject odd widths.
        int width = Math.Clamp((int)(_surface.ActualWidth * scale) & ~1, MinimumDesktopSize, MaximumDesktopSize);
        int height = Math.Clamp((int)(_surface.ActualHeight * scale) & ~1, MinimumDesktopSize, MaximumDesktopSize);
        return (width, height);
    }

    private void Configure(string host, int port)
    {
        var control = _control!;
        _desktopSize = MeasureDesktop();
        control.Set("Server", host);
        string user = _connection.User.Length > 0 ? _connection.User : _device.User;
        if (user.Length > 0) control.Set("UserName", user);
        control.TrySet("Domain", _connection.Domain);
        control.Set("DesktopWidth", _desktopSize.Width);
        control.Set("DesktopHeight", _desktopSize.Height);
        control.TrySet("ColorDepth", 32);
        control.TrySet("ConnectingText", "正在连接远程桌面…");
        control.TrySet("DisconnectedText", "已断开");

        using var settings = AdvancedSettings(control);
        settings.Set("RDPPort", port);
        settings.TrySet("EnableCredSspSupport", true);
        // 2: warn about a server identity that cannot be verified, and let the user decide.
        settings.TrySet("AuthenticationLevel", 2u);
        settings.TrySet("SmartSizing", true);
        settings.TrySet("RedirectClipboard", _connection.RedirectClipboard);
        settings.TrySet("RedirectDrives", _connection.RedirectDrives);
        settings.TrySet("GrabFocusOnConnect", true);
        settings.TrySet("EnableWindowsKey", 1);
        settings.TrySet("BitmapPersistence", 1);

        string? password = _app.Store.Secrets.Get(_connection.PasswordSecretId) ??
            (_device.Auth == AuthMethod.Password ? _app.Store.Secrets.Get(_device.PasswordSecretId) : null);
        if (password != null) SetPassword(settings, password);
    }

    private static ComDispatch AdvancedSettings(ComDispatch control)
    {
        foreach (var name in new[] { "AdvancedSettings9", "AdvancedSettings8", "AdvancedSettings7", "AdvancedSettings2" })
        {
            try
            {
                return control.GetObject(name);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or NotImplementedException)
            {
            }
        }
        throw new InvalidOperationException("远程桌面控件版本过旧。");
    }

    private void SetPassword(ComDispatch settings, string password)
    {
        if (settings.TrySet("ClearTextPassword", password)) return;
        // IMsTscNonScriptable derives directly from IUnknown; put_ClearTextPassword is its first method.
        Guid id = NonScriptableId;
        if (_nonScriptable == 0 || Marshal.QueryInterface(_nonScriptable, in id, out nint pointer) < 0) return;
        nint text = Marshal.StringToBSTR(password);
        try
        {
            unsafe
            {
                var call = (delegate* unmanaged[Stdcall]<nint, nint, int>)(*(nint**)pointer)[3];
                call(pointer, text);
            }
        }
        finally
        {
            Marshal.FreeBSTR(text);
            Marshal.Release(pointer);
        }
    }

    // ---- Following the connection ------------------------------------------------------------

    private void PollState()
    {
        if (_control == null) return;
        int state;
        try
        {
            state = _control.GetInt("Connected");
        }
        catch (COMException)
        {
            return;
        }
        if (state == _lastState) return;
        int previous = _lastState;
        _lastState = state;
        _connecting = false;

        if (state == ConnectedDisconnected && previous != ConnectedDisconnected)
        {
            int reason = 0;
            try
            {
                reason = _control.GetInt("ExtendedDisconnectReason");
            }
            catch (COMException)
            {
            }
            string message = _everConnected ? "远程桌面已断开" : "未能连接到远程桌面。请检查地址、端口和账号，并确认对方已启用远程桌面。";
            Fail(reason != 0 ? $"{message}（原因代码 {reason}）" : message);
            return;
        }

        if (state == 1)
        {
            _everConnected = true;
            _overlay.Visibility = Visibility.Collapsed;
            _status.Text = "已连接 · " + _device.Name;
        }
        UpdateWindowVisibility();
        StateChanged?.Invoke();
    }

    private void SyncWindowRect()
    {
        if (_window == 0 || XamlRoot == null || !_active || _surface.ActualWidth <= 0) return;
        double scale = XamlRoot.RasterizationScale;
        var origin = _surface.TransformToVisual(null).TransformPoint(default);
        var rect = ((int)Math.Round(origin.X * scale), (int)Math.Round(origin.Y * scale), (int)Math.Round(_surface.ActualWidth * scale), (int)Math.Round(_surface.ActualHeight * scale));
        if (rect == _lastRect) return;
        bool resized = rect.Item3 != _lastRect.Width || rect.Item4 != _lastRect.Height;
        _lastRect = rect;
        SetWindowPos(_window, TopWindow, rect.Item1, rect.Item2, rect.Item3, rect.Item4, 0x0010); // SWP_NOACTIVATE
        if (resized) _resize.Start();
    }

    /// <summary>Asks the running session to take the new window size; smart sizing covers servers that decline.</summary>
    private void ApplyDesktopSize()
    {
        if (_control == null || _lastState != 1) return;
        var size = MeasureDesktop();
        if (size == _desktopSize) return;
        _desktopSize = size;
        try
        {
            _control.Call("UpdateSessionDisplaySettings", (uint)size.Width, (uint)size.Height, (uint)size.Width, (uint)size.Height, 0u, 100u, 100u);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or NotImplementedException)
        {
            // Older servers keep the size chosen at connect time.
        }
    }

    private void Disconnect()
    {
        _attempt++;
        Fail("已断开。");
    }

    private void TearDownConnection()
    {
        _poll.Stop();
        _resize.Stop();
        _connecting = false;
        _lastState = ConnectedDisconnected;
        _lastRect = default;
        if (_control != null)
        {
            try
            {
                if (_control.GetInt("Connected") != ConnectedDisconnected) _control.Call("Disconnect");
            }
            catch (COMException)
            {
            }
        }
        // A fresh control per attempt: its properties cannot be changed once it has connected.
        DestroyControl();
        _tunnel?.Dispose();
        _tunnel = null;
    }

    private void DestroyControl()
    {
        _control?.Dispose();
        _control = null;
        if (_nonScriptable != 0)
        {
            Marshal.Release(_nonScriptable);
            _nonScriptable = 0;
        }
        if (_window != 0)
        {
            DestroyWindow(_window);
            _window = 0;
        }
    }

    private async Task OpenExternallyAsync()
    {
        try
        {
            var route = await _app.OpenRdpRouteAsync(_device, _connection);
            var target = new RdpTarget(route.Host, route.Port, _connection.User.Length > 0 ? _connection.User : _device.User, _connection.Domain, _connection.RedirectClipboard, _connection.RedirectDrives);
            string path = Path.Combine(Path.GetTempPath(), "easyterminal-" + Guid.NewGuid().ToString("N") + ".rdp");
            await File.WriteAllTextAsync(path, RdpFile.Build(target));
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mstsc.exe")) { ArgumentList = { path }, UseShellExecute = false });
            if (process != null) await process.WaitForExitAsync();
            File.Delete(path);
            route.Tunnel?.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await Dialogs.AlertAsync("无法打开系统远程桌面", ex.Message);
        }
    }

    public void Dispose()
    {
        _attempt++;
        Open.Remove(this);
        TearDownConnection();
    }
}
