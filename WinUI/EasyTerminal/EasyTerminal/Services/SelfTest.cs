#if DEBUG
using System.Runtime.InteropServices.WindowsRuntime;
using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Core.Terminal;
using EasyTerminal.Pages;
using EasyTerminal.Sessions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace EasyTerminal.Services;

/// <summary>
/// Debug-only walkthrough, enabled by a marker file: visits each page, drives real sessions and writes
/// PNGs plus a log, so the UI can be checked without controlling the desktop. It runs on its own data directory, never the real configuration.
/// </summary>
internal static class SelfTest
{
    public static string MarkerPath => Path.Combine(AppStore.DefaultDirectory, "selftest.on");
    private static string OutputDirectory => Path.Combine(AppStore.DefaultDirectory, "selftest");
    public static string DataDirectory => Path.Combine(OutputDirectory, "data");

    public static bool IsEnabled => File.Exists(MarkerPath);

    public static async Task RunAsync(MainWindow window, FrameworkElement root, SessionManager sessions)
    {
        Directory.CreateDirectory(OutputDirectory);
        string log = Path.Combine(OutputDirectory, "log.txt");
        File.WriteAllText(log, $"start {DateTime.Now:O}\npath={AppServices.Current.HelperPath}\n");
        void Log(string text) => File.AppendAllText(log, text + "\n");
        var app = AppServices.Current;

        async Task Step(string name, Func<Task> body)
        {
            try
            {
                await body();
                Log("OK   " + name);
            }
            catch (Exception ex)
            {
                Log($"FAIL {name}: {ex}");
            }
        }

        string ScreenOf(TerminalSession session)
        {
            var buffer = session.Buffer;
            return buffer.GetText(buffer.AbsoluteViewportRow(0), 0, buffer.AbsoluteViewportRow(buffer.Rows - 1), buffer.Cols - 1).Trim();
        }

        const string sampleName = "示例服务器";
        void RemoveSamples()
        {
            app.Store.Data.Devices.RemoveAll(d => d.Name == sampleName);
            app.Store.Save();
        }

        await Task.Delay(1200);

        RemoveSamples();
        var sample = new Device { Name = sampleName, Host = "127.0.0.1", Port = 1, User = "demo" };
        sample.Network = new NetworkProfile { ProxyKind = ProxyKind.None };
        var ssh = new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh };
        var sftp = new Connection { Name = "SFTP", Protocol = ConnectionProtocol.Sftp };
        var mosh = new Connection { Name = "Mosh", Protocol = ConnectionProtocol.Mosh };
        var tmux = new Connection { Name = "工作区", Protocol = ConnectionProtocol.Tmux, SessionName = "work" };
        var tmuxList = new Connection { Name = "tmux", Protocol = ConnectionProtocol.Tmux };
        var docker = new Connection { Name = "Docker", Protocol = ConnectionProtocol.Docker };
        var proxmox = new Connection { Name = "Proxmox", Protocol = ConnectionProtocol.Proxmox };
        var performance = new Connection { Name = "状态", Protocol = ConnectionProtocol.Monitor };
        sample.Connections.AddRange(new[] { ssh, sftp, mosh, tmux, tmuxList, docker, proxmox, performance });
        app.Store.Data.Devices.Add(sample);
        app.Probes[sample.Id] = RemoteProbe.Parse("@@ENV\ntermux\ntmux\n@@SYS\nhost=pve1\nos=Debian GNU/Linux 12 (bookworm)\nkernel=Linux 6.8\narch=x86_64\ncpus=16\nmemkb=65536000\nload=0.50 1.25 2.00\nuptime=864000\n@@TMUX\n3|1|1700000000|main\n@@TMUXWIN\n0\t1\t1\tvim\tmain\teditor\n1\t3\t0\tbash\tmain\tlogs\n@@HERDR\n{\"sessions\":[{\"name\":\"agents\",\"default\":false,\"running\":true}]}\n@@DOCKER\n{\"ID\":\"c1\",\"Names\":\"nginx-web\",\"Image\":\"nginx:latest\",\"State\":\"running\"}\n{\"ID\":\"c2\",\"Names\":\"postgres\",\"Image\":\"postgres:16\",\"State\":\"exited\"}\n@@PVE\nnode=pve1\nsudo=\nuid=0\n@@PCT\nVMID Status Lock Name\n101 running  dns\n102 stopped  build\n@@QM\nVMID NAME STATUS MEM(MB) BOOTDISK(GB) PID\n200 win11 running 8192 64.00 1234\n@@TOOLS\nhtop\nbtop\nncdu\nmtr\niftop\nvnstat\npm=apt-get\n@@GEO\n{\"ip\":\"203.0.113.5\",\"city\":\"Tokyo\",\"region\":\"Tokyo\",\"country\":\"JP\",\"org\":\"AS64500 Example\"}\n@@END\n");
        sample.Facts = app.Probes[sample.Id].Facts;
        sample.Group = "实验室/东京";
        app.Store.Data.DeviceFolders = new List<string> { "生产" };
        sample.Tags = new List<string> { "pve", "东京" };
        app.Store.Data.Snippets.Clear();
        app.Store.Data.Snippets.Add(new Snippet { Name = "查看容器日志", Group = "Docker", Description = "跟随最新输出", Body = "docker logs -f --tail 100 {{容器}}" });
        app.Store.Data.Snippets.Add(new Snippet { Name = "磁盘占用", Body = "df -h\ndu -sh /var/log/*" });
        app.Store.Data.PortForwards.Clear();
        app.Store.Data.PortForwards.Add(new PortForward { Name = "数据库", DeviceId = sample.Id, BindPort = 15432, TargetHost = "127.0.0.1", TargetPort = 5432 });
        app.Store.Data.PortForwards.Add(new PortForward { Name = "代理", Kind = PortForwardKind.Dynamic, DeviceId = sample.Id, BindPort = 1080 });

        foreach (var page in new[] { "home", "devices", "snippets", "forwards", "keys", "settings" })
        {
            await Step("page " + page, async () =>
            {
                if (page == "home") window.Navigate(page);
                window.Navigate(page);
                await Task.Delay(800);
                await CaptureAsync(root, page);
            });
        }

        await Step("devices page folders and discovered guests", async () =>
        {
            window.Navigate("devices");
            await Task.Delay(500);
            var page = FindAll<DevicesPage>(root).First();
            var scroller = (ScrollViewer)page.Content;
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            await Task.Delay(800);
            await CaptureAsync(root, "devices-lower");
            scroller.ChangeView(null, 0, null, true);
        });

        await Step("home with every widget", async () =>
        {
            window.Navigate("home");
            await Task.Delay(500);
            var home = FindAll<Pages.HomePage>(root).First();
            home.ShowEveryWidgetForTest(false, sample.Id);
            await Task.Delay(1500);
            await CaptureAsync(root, "home-widgets");
            var scroller = (ScrollViewer)home.Content;
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            await Task.Delay(800);
            await CaptureAsync(root, "home-widgets-lower");
            scroller.ChangeView(null, scroller.ScrollableHeight / 2, null, true);
            await Task.Delay(800);
            await CaptureAsync(root, "home-widgets-middle");

            // Wide enough for panels to sit side by side on the twelve-column grid.
            var size = window.AppWindow.Size;
            window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1900, 1150));
            scroller.ChangeView(null, 0, null, true);
            await Task.Delay(1500);
            await CaptureAsync(root, "home-wide");
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            await Task.Delay(800);
            await CaptureAsync(root, "home-wide-lower");
            Log($"home wide width={home.ActualWidth:0}");
            window.AppWindow.Resize(size);
            await Task.Delay(500);

            scroller.ChangeView(null, 0, null, true);
            home.ShowEveryWidgetForTest(true, sample.Id);
            await Task.Delay(800);
            await CaptureAsync(root, "home-editing");
            var frames = FindAll<Pages.Home.WidgetFrame>(home).ToList();
            Log($"home widgets={frames.Count} width={home.ActualWidth:0}");
            foreach (var frame in frames.Where(f => f.View.Error != null)) Log($"FAIL widget {frame.View.Model.Kind}: {frame.View.Error}");
            home.ResetForTest();
            await Task.Delay(300);
        });

        await Step("settings tailscale section", async () =>
        {
            window.Navigate("settings");
            await Task.Delay(800);
            var page = FindAll<Pages.SettingsPage>(root).First();
            var scroller = (ScrollViewer)page.Content;
            var heading = FindAll<TextBlock>(page).First(t => t.Text.StartsWith("Tailscale", StringComparison.Ordinal));
            double top = heading.TransformToVisual((UIElement)scroller.Content).TransformPoint(default).Y;
            scroller.ChangeView(null, Math.Max(0, top - 12), null, true);
            await Task.Delay(800);
            Log($"tailscale helper available={Core.Tailscale.TailscaleNode.Shared.IsHelperAvailable}");
            await CaptureAsync(root, "settings-tailscale");
        });

        await Step("every bundled logo and flag loads", async () =>
        {
            var failed = new List<string>();
            int opened = 0;
            var strip = new Microsoft.UI.Xaml.Controls.VariableSizedWrapGrid { Orientation = Orientation.Horizontal, ItemWidth = 30, ItemHeight = 24 };
            foreach (var folder in new[] { "Os", "Flags" })
            {
                foreach (var file in Directory.EnumerateFiles(Path.Combine(AppContext.BaseDirectory, "Assets", folder), "*.svg"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    var image = Ui.Asset(folder, name, 24, 18)!;
                    var source = (Microsoft.UI.Xaml.Media.Imaging.SvgImageSource)image.Source;
                    source.Opened += (_, _) => opened++;
                    source.OpenFailed += (_, args) => failed.Add($"{folder}/{name}: {args.Status}");
                    strip.Children.Add(image);
                }
            }
            var host = new ScrollViewer { Content = strip, Width = 1000, Height = 600 };
            var overlay = (Grid)root;
            Grid.SetRowSpan(host, 2);
            overlay.Children.Add(host);
            await Task.Delay(2500);
            await CaptureAsync(host, "all-icons");
            overlay.Children.Remove(host);
            Log($"icons opened={opened} failed={failed.Count}" + (failed.Count > 0 ? "\n  " + string.Join("\n  ", failed) : ""));
            if (failed.Count > 0) throw new InvalidOperationException($"{failed.Count} icons failed to load");
        });

        foreach (var (connection, name) in new[] { (tmuxList, "tab-tmux"), (docker, "tab-docker"), (proxmox, "tab-proxmox") })
        {
            await Step("session tab " + name, async () =>
            {
                await sessions.OpenConnectionAsync(sample, connection);
                var tab = sessions.Tabs[^1];
                await Task.Delay(800);
                Log($"{name} status={tab.StatusText} confirmClose={tab.HasForegroundWork}");
                await CaptureAsync(root, name);
                if (tab.HasForegroundWork) throw new InvalidOperationException("a list should close without asking");
                sessions.Close(tab);
            });
        }

        await Step("status tab with network and tools", async () =>
        {
            // A real local listener, so the latency chart has something to measure.
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var reachable = new Device { Name = sampleName, Host = "127.0.0.1", Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port, Facts = sample.Facts };
            app.Store.Data.Devices.Add(reachable);
            var connection = new Connection { Name = "状态", Protocol = ConnectionProtocol.Monitor };
            reachable.Connections.Add(connection);
            await sessions.OpenConnectionAsync(reachable, connection);
            var tab = sessions.Tabs[^1];
            await Task.Delay(7000);
            Log($"status tab status={tab.StatusText}");
            var scroller = (ScrollViewer)((UserControl)tab.Content).Content;
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            await Task.Delay(800);
            await CaptureAsync(root, "tab-status-lower");
            sessions.Close(tab);
            app.Store.Data.Devices.Remove(reachable);
            listener.Stop();
        });

        await Step("monitor session tab", async () =>
        {
            await sessions.OpenConnectionAsync(sample, performance);
            var tab = sessions.Tabs[^1];
            var random = new Random(7);
            var now = DateTimeOffset.Now;
            ((MonitorPane)tab.Panel!).PreviewForTest(Enumerable.Range(0, 60).Select(i => new Core.Monitoring.MonitorSample(
                now.AddSeconds((i - 60) * 2), i == 0 ? null : 30 + 25 * Math.Sin(i / 6.0) + random.NextDouble() * 8,
                1.2, 1.0, 0.8, "2/412", 65536000, 28000000 - i * 40000, 8000000, 7500000,
                i == 20 ? null : 2_000_000 + 1_500_000 * Math.Sin(i / 4.0), i == 20 ? null : 400_000 + random.NextDouble() * 300_000,
                random.NextDouble() * 9_000_000, 5_000_000 + random.NextDouble() * 4_000_000,
                new[] { new Core.Monitoring.DiskUsage("/", 500_000_000, 230_000_000), new Core.Monitoring.DiskUsage("/var/lib/vz", 2_000_000_000, 1_900_000_000) },
                new[] { new Core.Monitoring.ProcessUsage("qemu-system-x86", 62.5, 12.5), new Core.Monitoring.ProcessUsage("pvedaemon", 1.2, 0.4) })));
            await Task.Delay(1000);
            await CaptureAsync(root, "tab-monitor");
            sessions.Close(tab);
            window.Navigate("devices");
            await Task.Delay(500);
            await CaptureAsync(root, "devices-after-tabs");
            var moreButton = FindAll<Button>(root).First(b => ToolTipService.GetToolTip(b) as string == "更多" && b.ActualOffsetY() > 300);
            moreButton.Flyout = null;
            var peer = new Microsoft.UI.Xaml.Automation.Peers.ButtonAutomationPeer(moreButton);
            peer.Invoke();
            await Task.Delay(600);
            await CapturePopupAsync(root, "device-more-menu");
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)) popup.IsOpen = false;
        });

        await Step("rdp control loads and reports an unreachable host", async () =>
        {
            var rdp = new Device { Name = sampleName, Host = "127.0.0.1" };
            var connection = new Connection { Name = "远程桌面", Protocol = ConnectionProtocol.Rdp, Port = 1, User = "demo", PasswordSecretId = app.Store.Secrets.Set(null, "selftest") };
            rdp.Connections.Add(connection);
            app.Store.Data.Devices.Add(rdp);
            await sessions.OpenConnectionAsync(rdp, connection);
            var tab = sessions.Tabs[^1];
            await Task.Delay(600);
            Log("rdp live while connecting=" + tab.IsLive);
            await WaitAsync(() => !tab.IsLive, 45000);
            Log("rdp ended, status=" + tab.StatusText);
            await CaptureAsync(root, "rdp-failed");
            sessions.Close(tab);
            app.Store.Data.Devices.Remove(rdp);
        });

        await Step("snippet side panel", async () =>
        {
            window.Navigate("home");
            var panel = window.ShowSnippetsForTest();
            await Task.Delay(700);
            await CaptureAsync(root, "titlebar");
            Log($"snippet panel pinned={panel.IsPinned} size={panel.ActualWidth}x{panel.ActualHeight}");
            await CaptureAsync(panel, "snippet-panel");
            window.ShowSnippetsForTest();
            await Task.Delay(300);
        });

        await Step("assistant web view", async () =>
        {
            window.Navigate("home");
            window.ShowAssistantForTest();
            await Task.Delay(9000);
            Log("assistant " + await window.AssistantForTest!.SavePreviewAsync(Path.Combine(OutputDirectory, "assistant-web.png")));
            await CaptureAsync(root, "assistant");
            window.ShowAssistantForTest();
        });

        await Step("terminal find, split view and broadcast", async () =>
        {
            await sessions.OpenLocalAsync(app.LocalTargets(LocalKind.Shell).First(t => t.Id == "cmd"));
            var first = sessions.Tabs[^1];
            await sessions.OpenLocalAsync(app.LocalTargets(LocalKind.Shell).First(t => t.Id == "cmd"));
            var second = sessions.Tabs[^1];
            await Task.Delay(2000);
            window.ShowSplit(first);
            await Task.Delay(800);
            await CaptureAsync(root, "split-view");

            sessions.SetBroadcasting(true);
            second.View!.SendForTest("echo broadcast-marker\r");
            await Task.Delay(1500);
            sessions.SetBroadcasting(false);
            string firstScreen = ScreenOf(first.Terminal!);
            if (!firstScreen.Contains("broadcast-marker", StringComparison.Ordinal)) throw new InvalidOperationException("broadcast did not reach the other terminal:\n" + firstScreen);
            if (!first.View!.Find("BROADCAST-marker", backward: true)) throw new InvalidOperationException("find did not locate the text");
            Log("find selected: " + first.View.SelectedText);
            if (!first.View.SelectedText.Equals("broadcast-marker", StringComparison.Ordinal)) throw new InvalidOperationException("find selected the wrong text");

            window.CloseSplit();
            sessions.Close(second);
            sessions.Close(first);
        });

        await Step("command palette", async () =>
        {
            window.Navigate("home");
            window.OpenCommandPalette("");
            await Task.Delay(700);
            await CaptureAsync(root, "palette-titlebar");
            await CapturePopupAsync(root, "palette-empty");
            window.OpenCommandPalette("pos");
            await Task.Delay(500);
            await CapturePopupAsync(root, "palette-query");
            window.OpenCommandPalette(">");
            await Task.Delay(500);
            await CapturePopupAsync(root, "palette-commands");
            window.CloseCommandPalette();
            await Task.Delay(200);
        });

        await Step("device dialog", async () =>
        {
            var pending = DeviceDialog.ShowAsync(sample);
            await CloseDialogAfterCapture(root, "device-dialog");
            await pending;
        });
        await Step("connection dialog", async () =>
        {
            var pending = ConnectionDialog.ShowAsync(sample, tmux);
            await CloseDialogAfterCapture(root, "connection-dialog");
            await pending;
        });

        await Step("rdp connection dialog", async () =>
        {
            var pending = ConnectionDialog.ShowAsync(sample, new Connection { Name = "远程桌面", Protocol = ConnectionProtocol.Rdp });
            await CloseDialogAfterCapture(root, "connection-dialog-rdp");
            await pending;
        });
        await Step("new device dialog", async () =>
        {
            var pending = DeviceDialog.ShowAsync(null);
            await CloseDialogAfterCapture(root, "device-dialog-new");
            await pending;
        });

        await Step("terminal over mica", async () =>
        {
            var settings = app.Store.Data.Settings;
            string scheme = settings.ColorScheme;
            int opacity = settings.TerminalOpacity;
            Pages.SettingsPage.ChooseScheme(settings, Controls.ColorScheme.MicaFollow);
            app.NotifySettingsChanged();
            Log($"mica scheme opacity={settings.TerminalOpacity}");
            await sessions.OpenLocalAsync(app.LocalTargets(LocalKind.Shell).First(t => t.Id == "cmd"));
            var tab = sessions.Tabs[^1];
            await Task.Delay(1800);
            tab.Terminal!.Write("echo Mica terminal & dir /w C:\\Windows\\System32\\drivers\\etc\r");
            await Task.Delay(1500);
            await tab.View!.SaveFrameAsync(Path.Combine(OutputDirectory, "terminal-mica.png"));
            // The swap chain is invisible to element captures, so an outside screen grab checks it during this pause.
            Log("MICA-HOLD start");
            await Task.Delay(6000);
            Log("MICA-HOLD end");
            sessions.Close(tab);
            settings.ColorScheme = scheme;
            settings.TerminalOpacity = opacity;
            app.NotifySettingsChanged();
        });

        await Step("local terminal rendering", async () =>
        {
            await sessions.OpenLocalAsync(app.LocalTargets(LocalKind.Shell).First(t => t.Id == "cmd"));
            var tab = sessions.Tabs[^1];
            await Task.Delay(1800);
            const string script = "$e=[char]27;Clear-Host;'hello 中文 日本語 한글 emoji 😀 ok';\"$e[31mred $e[32mgreen $e[33myellow $e[34mblue $e[35mmagenta $e[36mcyan $e[0m\";\"$e[1mbold$e[0m $e[3mitalic$e[0m $e[4munderline$e[0m $e[9mstrike$e[0m $e[7minverse$e[0m\";\"$e[38;2;255;128;0mtruecolor $e[48;2;0;80;160m background $e[0m\";'┌──┬──┐';'│中│文│';'└──┴──┘'";
            tab.Terminal!.Write("chcp 65001 >nul & powershell -NoProfile -EncodedCommand " + Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes("[Console]::OutputEncoding=[Text.Encoding]::UTF8;" + script)) + "\r");
            await Task.Delay(3500);
            Log($"terminal state={tab.Terminal.State} size={tab.Terminal.Buffer.Cols}x{tab.Terminal.Buffer.Rows}");
            Log("--- screen\n" + ScreenOf(tab.Terminal) + "\n---");
            await tab.View!.SaveFrameAsync(Path.Combine(OutputDirectory, "terminal.png"));
            Log("OPAQUE-HOLD start");
            await Task.Delay(5000);
            Log("OPAQUE-HOLD end");
            await CaptureAsync(root, "sidebar-local-session");
            await CaptureMenuAsync(root, window.CreateNewSessionMenu(null), "new-menu-local");
            await sessions.OpenRemoteSessionAsync(sample, app.Probes[sample.Id].Sessions[0]);
            await Task.Delay(800);
            await CaptureAsync(root, "sidebar-two-folders");
            await CaptureMenuAsync(root, window.CreateNewSessionMenu(sample), "new-menu-device");
            sessions.Close(sessions.Tabs[^1]);
            await Task.Delay(400);
            window.ActivateTab(tab);
            tab.View.SelectAll();
            await Task.Delay(200);
            await tab.View.SaveFrameAsync(Path.Combine(OutputDirectory, "terminal-selected.png"));
            sessions.Close(tab);
        });

        await Step("packaged ssh: config accepted, connection refused is reported", async () =>
        {
            await sessions.OpenConnectionAsync(sample, ssh);
            var tab = sessions.Tabs[^1];
            await WaitAsync(() => tab.Terminal!.State == SessionState.Exited, 20000);
            await Task.Delay(400);
            string screen = ScreenOf(tab.Terminal!);
            Log("--- ssh screen\n" + screen + "\n---");
            await tab.View!.SaveFrameAsync(Path.Combine(OutputDirectory, "ssh-refused.png"));
            if (screen.Contains("Bad owner", StringComparison.OrdinalIgnoreCase) || screen.Contains("Bad configuration", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ssh rejected the generated config");
            if (!screen.Contains("refused", StringComparison.OrdinalIgnoreCase) && !screen.Contains("拒绝")) throw new InvalidOperationException("unexpected ssh output");
            sessions.Close(tab);
        });

        await Step("multiple addresses: off-network address is skipped", async () =>
        {
            var multi = new Device { Name = sampleName, Host = "10.255.255.1", AlternateAddresses = { "127.0.0.1:1" }, Port = 22, User = "demo" };
            app.Store.Data.Devices.Add(multi);
            var connection = new Connection { Name = "SSH", Protocol = ConnectionProtocol.Ssh };
            multi.Connections.Add(connection);
            await sessions.OpenConnectionAsync(multi, connection);
            var tab = sessions.Tabs[^1];
            await WaitAsync(() => tab.Terminal!.State == SessionState.Exited, 20000);
            await Task.Delay(400);
            Log("--- multi-address screen\n" + ScreenOf(tab.Terminal!) + "\n---");
            await tab.View!.SaveFrameAsync(Path.Combine(OutputDirectory, "multi-address.png"));
            sessions.Close(tab);
            app.Store.Data.Devices.Remove(multi);
        });

        await Step("mosh bootstrap failure is reported", async () =>
        {
            await sessions.OpenConnectionAsync(sample, mosh);
            var tab = sessions.Tabs[^1];
            await WaitAsync(() => tab.Terminal!.State == SessionState.Exited, 30000);
            await Task.Delay(300);
            Log("--- mosh screen\n" + ScreenOf(tab.Terminal!) + "\n---");
            sessions.Close(tab);
        });

        await Step("sftp failure is reported", async () =>
        {
            await sessions.OpenConnectionAsync(sample, sftp);
            var tab = sessions.Tabs[^1];
            await Task.Delay(5000);
            Log("sftp connected=" + tab.Sftp!.IsConnected);
            await CaptureAsync(root, "sftp-failed");
            sessions.Close(tab);
        });

        await Step("packaged probe failure is reported", async () =>
        {
            try
            {
                await app.ProbeAsync(sample);
                throw new InvalidOperationException("probe unexpectedly succeeded");
            }
            catch (IOException ex)
            {
                Log("probe error: " + ex.Message);
            }
        });

        await Step("packaged keygen through askpass", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "et-selftest-" + Guid.NewGuid().ToString("N"));
            try
            {
                var key = await app.Ssh.GenerateKeyAsync(new KeyGenerationOptions { FileName = "k", Type = KeyType.Ed25519, Passphrase = "selftest pass", Comment = "t", Directory = directory }, "selftest", default);
                Log("generated " + key.Fingerprint);
                var check = await ProcessRunner.RunAsync(SshRunner.SshKeygenPath, new[] { "-y", "-P", "selftest pass", "-f", key.Path }, TimeSpan.FromSeconds(10));
                if (check.ExitCode != 0) throw new InvalidOperationException("key is not protected by the passphrase: " + check.Error);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        });

        await Step("restored clean state", () =>
        {
            RemoveSamples();
            app.Probes.Remove(sample.Id);
            app.Store.Data.Snippets.Clear();
            app.Store.Data.PortForwards.Clear();
            app.Store.Save();
            return Task.CompletedTask;
        });

        Log("done, open tabs=" + sessions.Tabs.Count);
        Application.Current.Exit();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static double ActualOffsetY(this UIElement element) => element.TransformToVisual(null).TransformPoint(default).Y;

    private static async Task WaitAsync(Func<bool> condition, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("Timed out waiting for the session to end");
            await Task.Delay(100);
        }
    }

    private static async Task CaptureMenuAsync(FrameworkElement root, MenuFlyout menu, string name)
    {
        menu.ShowAt(root);
        await Task.Delay(500);
        var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).FirstOrDefault(p => p.Child is FrameworkElement);
        if (popup?.Child is not FrameworkElement content) throw new InvalidOperationException("Menu did not open");
        await CaptureAsync(content, name);
        menu.Hide();
        await Task.Delay(300);
    }

    private static async Task CapturePopupAsync(FrameworkElement root, string name)
    {
        // Tooltips and the like are popups too; the one that matters is the largest.
        var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .Where(p => p.Child is FrameworkElement { ActualWidth: > 0, ActualHeight: > 0 })
            .OrderByDescending(p => ((FrameworkElement)p.Child).ActualWidth * ((FrameworkElement)p.Child).ActualHeight)
            .FirstOrDefault();
        if (popup?.Child is not FrameworkElement content) throw new InvalidOperationException("Popup did not open: " + name);
        await CaptureAsync(content, name);
    }

    private static async Task CloseDialogAfterCapture(FrameworkElement root, string name)
    {
        await Task.Delay(800);
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot))
        {
            if (popup.Child is ContentDialog dialog)
            {
                await CaptureAsync(dialog, name);
                dialog.Hide();
                return;
            }
        }
        throw new InvalidOperationException("Dialog did not open");
    }

    private static async Task CaptureAsync(FrameworkElement root, string name)
    {
        var target = new RenderTargetBitmap();
        await target.RenderAsync(root);
        var pixels = await target.GetPixelsAsync();
        var folder = await StorageFolder.GetFolderFromPathAsync(OutputDirectory);
        var file = await folder.CreateFileAsync(name + ".png", CreationCollisionOption.ReplaceExisting);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }
}
#endif
