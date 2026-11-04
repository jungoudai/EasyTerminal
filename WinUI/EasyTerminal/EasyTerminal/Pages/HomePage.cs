using EasyTerminal.Core.Models;
using EasyTerminal.Core.Tailscale;
using EasyTerminal.Pages.Home;
using EasyTerminal.Services;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EasyTerminal.Pages;

/// <summary>
/// A dashboard of panels the user picks: sessions, machines, recent connections, resource data, live
/// monitors, forwards, the local environment and Tailscale. Edit mode adds, removes, resizes and drags them.
/// </summary>
public sealed class HomePage : UserControl, IRefreshablePage, IHomeEditor
{
    private readonly AppServices _app = AppServices.Current;
    private readonly SessionManager _sessions;
    private readonly DashboardPanel _dashboard = new();
    private readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _hint = Ui.Secondary("拖动组件调整顺序；用 ⋯ 调整宽度，铅笔图标修改组件设置。", true);
    private readonly List<WidgetFrame> _frames = new();
    private bool _isLoaded;

    public bool IsEditing { get; private set; }

    private List<HomeWidget> Widgets => _app.Store.Data.Settings.HomeWidgets ??= HomeWidget.DefaultLayout();

    public HomePage(SessionManager sessions)
    {
        _sessions = sessions;
        var (root, content) = Ui.Page();
        Content = root;

        var header = Ui.Columns("*", "auto");
        header.Children.Add(Ui.Title("主页").InColumn(0));
        header.Children.Add(_toolbar.InColumn(1));
        content.Children.Add(header);
        content.Children.Add(_hint);
        content.Children.Add(_dashboard);

        _app.ScanChanged += Refresh;
        _app.DevicesChanged += Refresh;
        _app.ForwardsChanged += Refresh;
        TailscaleNode.Shared.StateChanged += () => DispatcherQueue.TryEnqueue(Refresh);
        Loaded += (_, _) =>
        {
            _isLoaded = true;
            foreach (var frame in _frames) frame.View.Loaded();
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            _isLoaded = false;
            foreach (var frame in _frames) frame.View.Unloaded();
        };

        BuildToolbar();
        Rebuild();
    }

    public void Refresh()
    {
        foreach (var frame in _frames)
        {
            frame.View.Refresh();
            frame.UpdateChrome();
        }
    }

    // ---- Layout ------------------------------------------------------------------------------

    private void Rebuild()
    {
        foreach (var frame in _frames) frame.View.Dispose();
        _frames.Clear();
        _dashboard.Children.Clear();
        foreach (var widget in Widgets)
        {
            var frame = new WidgetFrame(HomeWidgetView.Create(widget, _sessions), this);
            _frames.Add(frame);
            _dashboard.Children.Add(frame);
            frame.View.Refresh();
            if (_isLoaded) frame.View.Loaded();
        }
        if (Widgets.Count == 0)
        {
            var empty = new StackPanel { Spacing = 8 };
            empty.Children.Add(Ui.Secondary("主页上还没有组件。"));
            empty.Children.Add(Ui.Button("添加组件", Ui.GlyphAdd, () => SetEditing(true), true));
            _dashboard.Children.Add(Ui.Card(empty));
        }
        _hint.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save() => _app.Store.Save();

    private void BuildToolbar()
    {
        _toolbar.Children.Clear();
        if (!IsEditing)
        {
            _toolbar.Children.Add(Ui.Button("自定义", Ui.GlyphEdit, () => SetEditing(true)));
            return;
        }
        var add = Ui.MenuButton(Ui.GlyphAdd, "添加组件", menu =>
        {
            foreach (var (kind, name, glyph, width) in HomeWidgetView.Catalog)
                menu.Items.Add(Ui.MenuItem(name, glyph, () => _ = AddAsync(kind, width)));
        });
        var addLabel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addLabel.Children.Add(Ui.Icon(Ui.GlyphAdd));
        addLabel.Children.Add(new TextBlock { Text = "添加组件" });
        add.Content = addLabel;
        // A menu button is an icon button by default; this one sits among labelled buttons.
        add.Style = (Style)Application.Current.Resources["DefaultButtonStyle"];
        add.Padding = new Thickness(11, 5, 11, 6);
        _toolbar.Children.Add(add);
        _toolbar.Children.Add(Ui.Button("恢复默认", Ui.GlyphRefresh, () => _ = ResetAsync()));
        _toolbar.Children.Add(Ui.Button("完成", Ui.GlyphAccept, () => SetEditing(false), true));
    }

    private void SetEditing(bool editing)
    {
        IsEditing = editing;
        BuildToolbar();
        _hint.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (Widgets.Count == 0) Rebuild();
        foreach (var frame in _frames) frame.UpdateChrome();
    }

    private async Task AddAsync(HomeWidgetKind kind, int width)
    {
        var widget = new HomeWidget { Kind = kind, Width = width, MaxItems = kind == HomeWidgetKind.Recent ? 6 : 8 };
        // A monitor is useless until it knows which machine to watch, so ask straight away.
        if (kind == HomeWidgetKind.LiveMonitor)
        {
            widget.DeviceId = _app.Store.Data.Devices.FirstOrDefault(DevicesPage.SupportsSsh)?.Id;
            if (!await EditOptionsAsync(widget)) return;
        }
        Widgets.Add(widget);
        Save();
        Rebuild();
    }

    private async Task ResetAsync()
    {
        if (!await Dialogs.ConfirmAsync("恢复默认主页？", "当前的组件和排列会被默认布局替换。", "恢复默认")) return;
        _app.Store.Data.Settings.HomeWidgets = HomeWidget.DefaultLayout();
        Save();
        Rebuild();
    }

    public void Remove(HomeWidget widget)
    {
        Widgets.Remove(widget);
        Save();
        Rebuild();
    }

    public void Resize(HomeWidget widget, int width)
    {
        widget.Width = width;
        Save();
        _dashboard.InvalidateMeasure();
    }

    public void Move(HomeWidget widget, int delta)
    {
        int index = Widgets.IndexOf(widget);
        int target = Math.Clamp(index + delta, 0, Widgets.Count - 1);
        if (index < 0 || target == index) return;
        Widgets.RemoveAt(index);
        Widgets.Insert(target, widget);
        Save();
        Rebuild();
    }

    public void MoveNextTo(Guid dragged, HomeWidget target, bool after)
    {
        var widget = Widgets.FirstOrDefault(w => w.Id == dragged);
        if (widget == null || widget == target) return;
        Widgets.Remove(widget);
        int index = Widgets.IndexOf(target) + (after ? 1 : 0);
        Widgets.Insert(Math.Clamp(index, 0, Widgets.Count), widget);
        Save();
        Rebuild();
    }

    public async Task ConfigureAsync(HomeWidget widget)
    {
        if (!await EditOptionsAsync(widget)) return;
        Save();
        Rebuild();
    }

#if DEBUG
    /// <summary>Puts one of every panel on the page, for the self-test's screenshots.</summary>
    internal void ShowEveryWidgetForTest(bool editing, Guid? monitorDevice)
    {
        _app.Store.Data.Settings.HomeWidgets = HomeWidgetView.Catalog
            .Select(c => new HomeWidget { Kind = c.Kind, Width = c.Width, DeviceId = c.Kind == HomeWidgetKind.LiveMonitor ? monitorDevice : null })
            .ToList();
        Rebuild();
        SetEditing(editing);
    }

    internal void ResetForTest()
    {
        _app.Store.Data.Settings.HomeWidgets = null;
        SetEditing(false);
        Rebuild();
    }
#endif

    // ---- Panel options -----------------------------------------------------------------------

    private async Task<bool> EditOptionsAsync(HomeWidget widget)
    {
        var probe = HomeWidgetView.Create(widget, _sessions);
        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };

        var title = new TextBox { Header = "标题", Text = widget.Title, PlaceholderText = probe.DefaultTitle };
        panel.Children.Add(title);

        ComboBox? group = null;
        if (probe.UsesGroup)
        {
            group = new ComboBox { Header = "显示哪个分组的设备", HorizontalAlignment = HorizontalAlignment.Stretch };
            group.Items.Add(new ComboBoxItem { Content = "全部设备", Tag = "" });
            foreach (var name in _app.Store.Data.Devices.Select(d => d.Group).Where(g => g.Length > 0).Distinct().Order())
                group.Items.Add(new ComboBoxItem { Content = name, Tag = name });
            group.SelectedItem = group.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == widget.Group) ?? group.Items[0];
            panel.Children.Add(group);
        }

        ComboBox? device = null;
        if (probe.UsesDevice)
        {
            device = new ComboBox { Header = "监控哪台设备（通过 SSH 每 2 秒采样，主页打开时运行）", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var d in _app.Store.Data.Devices.Where(DevicesPage.SupportsSsh))
                device.Items.Add(new ComboBoxItem { Content = $"{d.Name} ({d.Host})", Tag = d.Id });
            device.SelectedItem = device.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (Guid)i.Tag == widget.DeviceId) ?? device.Items.FirstOrDefault();
            panel.Children.Add(device);
        }

        NumberBox? max = null;
        if (probe.UsesMaxItems)
        {
            max = new NumberBox { Header = "最多显示条数", Minimum = 1, Maximum = 50, Value = widget.MaxItems, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
            panel.Children.Add(max);
        }

        var width = new ComboBox { Header = "宽度", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var (w, name) in WidgetFrame.Widths) width.Items.Add(new ComboBoxItem { Content = name, Tag = w });
        width.SelectedItem = width.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == widget.Width) ?? width.Items[2];
        panel.Children.Add(width);
        probe.Dispose();

        var dialog = new ContentDialog
        {
            Title = "组件设置",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;

        if (device != null && device.SelectedItem is not ComboBoxItem)
        {
            await Dialogs.AlertAsync("没有可监控的设备", "实时监控需要一台可以 SSH 登录的设备。");
            return false;
        }
        widget.Title = title.Text.Trim();
        if (group?.SelectedItem is ComboBoxItem { Tag: string g }) widget.Group = g;
        if (device?.SelectedItem is ComboBoxItem { Tag: Guid id }) widget.DeviceId = id;
        if (max != null && !double.IsNaN(max.Value)) widget.MaxItems = (int)max.Value;
        if (width.SelectedItem is ComboBoxItem { Tag: int chosen }) widget.Width = chosen;
        return true;
    }
}
