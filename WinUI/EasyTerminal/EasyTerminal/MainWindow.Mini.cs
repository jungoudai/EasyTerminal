using EasyTerminal.Sessions;
using Microsoft.UI.Windowing;

namespace EasyTerminal;

/// <summary>Running terminals shown in small always-on-top windows, one per session at most.</summary>
public sealed partial class MainWindow
{
    private readonly Dictionary<SessionTab, MiniWindow> _miniWindows = new();

    private bool HasMiniWindow(SessionTab tab) => _miniWindows.ContainsKey(tab);

    private void ToggleMiniWindow(SessionTab tab)
    {
        if (_miniWindows.TryGetValue(tab, out var open)) open.Close();
        else ShowMiniWindow(tab);
    }

    private void ShowMiniWindow(SessionTab tab)
    {
        if (tab.Terminal == null) return;
        if (_miniWindows.TryGetValue(tab, out var existing))
        {
            existing.Activate();
            return;
        }

        var mini = new MiniWindow(tab, _app.Appearance);
        _miniWindows[tab] = mini;
        mini.ReturnRequested += () =>
        {
            mini.Close();
            BringToFront();
            ActivateTab(tab);
        };
        mini.Closed += (_, _) =>
        {
            _miniWindows.Remove(tab);
            ApplyTabState(tab);
        };
        mini.Activate();
        ApplyTabState(tab);
    }

    private void BringToFront()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter) presenter.Restore();
        Activate();
    }

    private void CloseMiniWindow(SessionTab tab)
    {
        if (_miniWindows.TryGetValue(tab, out var mini)) mini.Close();
    }

    private void CloseAllMiniWindows()
    {
        foreach (var mini in _miniWindows.Values.ToList()) mini.Close();
    }

    private void UpdateMiniWindow(SessionTab tab)
    {
        if (_miniWindows.TryGetValue(tab, out var mini)) mini.UpdateTitle();
    }

#if DEBUG
    internal MiniWindow ShowMiniWindowForTest(SessionTab tab)
    {
        ShowMiniWindow(tab);
        return _miniWindows[tab];
    }

    internal int MiniWindowCountForTest => _miniWindows.Count;
#endif

    private void RefreshMiniWindowAppearance()
    {
        var appearance = _app.Appearance;
        foreach (var mini in _miniWindows.Values) mini.SetAppearance(appearance);
    }
}
