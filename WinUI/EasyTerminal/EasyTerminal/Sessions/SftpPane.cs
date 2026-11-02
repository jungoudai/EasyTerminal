using EasyTerminal.Core.Models;
using EasyTerminal.Core.Ssh;
using EasyTerminal.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EasyTerminal.Sessions;

/// <summary>Remote file browser: virtualized listing, pipelined transfers and Explorer drag-and-drop.</summary>
public sealed class SftpPane : Grid, IDisposable
{
    private const string RowTemplate =
        "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\">" +
        "<Grid ColumnSpacing=\"12\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"20\"/><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"90\"/><ColumnDefinition Width=\"150\"/><ColumnDefinition Width=\"100\"/></Grid.ColumnDefinitions>" +
        "<FontIcon FontSize=\"14\"/><TextBlock Grid.Column=\"1\" TextTrimming=\"CharacterEllipsis\" VerticalAlignment=\"Center\"/>" +
        "<TextBlock Grid.Column=\"2\" FontSize=\"12\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Opacity=\"0.7\"/>" +
        "<TextBlock Grid.Column=\"3\" FontSize=\"12\" VerticalAlignment=\"Center\" Opacity=\"0.7\"/>" +
        "<TextBlock Grid.Column=\"4\" FontSize=\"12\" FontFamily=\"Cascadia Mono\" VerticalAlignment=\"Center\" Opacity=\"0.7\"/>" +
        "</Grid></DataTemplate>";

    private readonly Device _device;
    private readonly Connection _connection;
    private readonly AppServices _app = AppServices.Current;
    private readonly ListView _list = new() { SelectionMode = ListViewSelectionMode.Extended, AllowDrop = true, IsItemClickEnabled = false };
    private readonly TextBox _path = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = Ui.Secondary("");
    private readonly ProgressBar _progress = new() { Visibility = Visibility.Collapsed };
    private readonly InfoBar _error = new() { Severity = InfoBarSeverity.Error, IsOpen = false };
    private readonly Button _cancel;
    private SftpClient? _client;
    private string _current = "";
    private CancellationTokenSource? _transfer;

    public bool IsConnected => _client != null;
    public event Action? StateChanged;

    public SftpPane(Device device, Connection connection)
    {
        _device = device;
        _connection = connection;

        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Padding = new Thickness(12);
        RowSpacing = 8;

        var toolbar = Ui.Columns("auto", "auto", "*", "auto", "auto", "auto", "auto", "auto");
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphUp, "上一级", () => _ = NavigateAsync(ParentOf(_current))).InColumn(0));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphRefresh, "刷新", () => _ = NavigateAsync(_current)).InColumn(1));
        toolbar.Children.Add(_path.InColumn(2));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphUpload, "上传文件", () => _ = PickAndUploadAsync()).InColumn(3));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphDownload, "下载所选", () => _ = DownloadSelectedAsync()).InColumn(4));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphAdd, "新建文件夹", () => _ = MakeDirectoryAsync()).InColumn(5));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphEdit, "重命名", () => _ = RenameSelectedAsync()).InColumn(6));
        toolbar.Children.Add(Ui.IconButton(Ui.GlyphDelete, "删除所选", () => _ = DeleteSelectedAsync()).InColumn(7));
        Children.Add(toolbar);

        Grid.SetRow(_error, 1);
        Children.Add(_error);

        _list.ItemTemplate = (DataTemplate)XamlReader.Load(RowTemplate);
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.DoubleTapped += (_, _) => OpenSelected();
        _list.DragOver += (_, e) =>
        {
            if (e.DataView.Contains(StandardDataFormats.StorageItems))
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                e.DragUIOverride.Caption = "上传到 " + _current;
            }
        };
        _list.Drop += OnDrop;
        Grid.SetRow(_list, 2);
        Children.Add(_list);

        _cancel = Ui.Button("取消", null, () => _transfer?.Cancel());
        _cancel.Visibility = Visibility.Collapsed;
        var footer = Ui.Columns("*", "200", "auto");
        footer.Children.Add(_status.InColumn(0));
        _progress.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(_progress.InColumn(1));
        footer.Children.Add(_cancel.InColumn(2));
        Grid.SetRow(footer, 3);
        Children.Add(footer);

        _path.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter) _ = NavigateAsync(_path.Text.Trim());
        };
    }

    public async void Reconnect()
    {
        Disconnect();
        _status.Text = $"正在连接 {_device.Host}…";
        _error.IsOpen = false;
        try
        {
            _client = await _app.OpenSftpAsync(_device, _connection);
            StateChanged?.Invoke();
            string start = string.IsNullOrWhiteSpace(_connection.Command) ? "." : _connection.Command.Trim();
            await NavigateAsync(await _client.RealPathAsync(start));
        }
        catch (Exception ex)
        {
            ShowError("无法连接 SFTP", ex.Message);
            _status.Text = "未连接";
            Disconnect();
            StateChanged?.Invoke();
        }
    }

    private void Disconnect()
    {
        _client?.Dispose();
        _client = null;
    }

    public void Dispose()
    {
        _transfer?.Cancel();
        Disconnect();
    }

    private void ShowError(string title, string message)
    {
        _error.Title = title;
        _error.Message = message;
        _error.IsOpen = true;
    }

    private static string ParentOf(string path)
    {
        if (path.Length <= 1) return "/";
        int slash = path.TrimEnd('/').LastIndexOf('/');
        return slash <= 0 ? "/" : path[..slash];
    }

    private static string Join(string directory, string name) => directory.EndsWith('/') ? directory + name : directory + "/" + name;

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not SftpEntry entry || args.ItemContainer.ContentTemplateRoot is not Grid row) return;
        ((FontIcon)row.Children[0]).Glyph = entry.IsDirectory ? Ui.GlyphFolder : Ui.GlyphFile;
        ((TextBlock)row.Children[1]).Text = entry.Name;
        ((TextBlock)row.Children[2]).Text = entry.IsDirectory ? "" : Ui.FormatSize(entry.Size);
        ((TextBlock)row.Children[3]).Text = entry.Modified?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? "";
        ((TextBlock)row.Children[4]).Text = entry.PermissionText;
    }

    public async Task NavigateAsync(string path)
    {
        var client = _client;
        if (client == null) return;
        try
        {
            string real = await client.RealPathAsync(path);
            var entries = await client.ListAsync(real);
            _current = real;
            _path.Text = real;
            _list.ItemsSource = entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            _status.Text = $"{entries.Count} 个项目";
            _error.IsOpen = false;
        }
        catch (Exception ex)
        {
            HandleFailure("无法打开目录", ex);
        }
    }

    private void HandleFailure(string title, Exception ex)
    {
        ShowError(title, ex.Message);
        if (ex is IOException and not SftpException)
        {
            // The transport itself is gone, not just this operation.
            Disconnect();
            _status.Text = "连接已断开";
            StateChanged?.Invoke();
        }
    }

    private void OpenSelected()
    {
        if (_list.SelectedItem is SftpEntry { IsDirectory: true } entry) _ = NavigateAsync(entry.FullPath);
    }

    private List<SftpEntry> Selection => _list.SelectedItems.OfType<SftpEntry>().ToList();

    private async Task RunTransferAsync(string label, Func<IProgress<long>, Action<long>, CancellationToken, Task> work)
    {
        if (_transfer != null)
        {
            ShowError("请稍候", "已有传输正在进行。");
            return;
        }
        _transfer = new CancellationTokenSource();
        _progress.Visibility = Visibility.Visible;
        _cancel.Visibility = Visibility.Visible;
        _progress.IsIndeterminate = true;
        _status.Text = label;
        long total = 0;
        var progress = new Progress<long>(done =>
        {
            if (total > 0)
            {
                _progress.IsIndeterminate = false;
                _progress.Value = Math.Min(100, done * 100.0 / total);
            }
            _status.Text = $"{label}  {Ui.FormatSize(done)}" + (total > 0 ? $" / {Ui.FormatSize(total)}" : "");
        });
        try
        {
            await work(progress, value => total = value, _transfer.Token);
            _status.Text = label + " 完成";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "已取消";
        }
        catch (Exception ex)
        {
            HandleFailure(label + " 失败", ex);
        }
        finally
        {
            _transfer.Dispose();
            _transfer = null;
            _progress.Visibility = Visibility.Collapsed;
            _cancel.Visibility = Visibility.Collapsed;
        }
    }

    private async Task UploadPathsAsync(IReadOnlyList<string> paths)
    {
        var client = _client;
        if (client == null || paths.Count == 0) return;
        string target = _current;
        await RunTransferAsync($"上传到 {target}", async (progress, setTotal, cancellation) =>
        {
            var files = new List<(string Local, string Remote, long Size)>();
            var directories = new List<string>();
            foreach (var path in paths) CollectUpload(path, Join(target, Path.GetFileName(path.TrimEnd('\\'))), files, directories);
            long total = files.Sum(f => f.Size);
            setTotal(total);

            foreach (var directory in directories)
            {
                try
                {
                    await client.MakeDirectoryAsync(directory);
                }
                catch (SftpException)
                {
                    // Already exists is fine; a real failure surfaces on the first file below it.
                }
            }

            long done = 0;
            foreach (var (local, remote, size) in files)
            {
                await using var stream = File.OpenRead(local);
                long start = done;
                await client.UploadAsync(stream, remote, new Progress<long>(v => progress.Report(start + v)), cancellation);
                done += size;
            }
        });
        await NavigateAsync(_current);
    }

    private static void CollectUpload(string local, string remote, List<(string, string, long)> files, List<string> directories)
    {
        if (Directory.Exists(local))
        {
            directories.Add(remote);
            foreach (var entry in Directory.EnumerateFileSystemEntries(local))
                CollectUpload(entry, Join(remote, Path.GetFileName(entry)), files, directories);
        }
        else
        {
            files.Add((local, remote, new FileInfo(local).Length));
        }
    }

    private async Task PickAndUploadAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        await UploadPathsAsync(files.Select(f => f.Path).ToList());
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        var items = await e.DataView.GetStorageItemsAsync();
        deferral.Complete();
        await UploadPathsAsync(items.Select(i => i.Path).ToList());
    }

    private async Task DownloadSelectedAsync()
    {
        var client = _client;
        var selected = Selection;
        if (client == null || selected.Count == 0) return;
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, MainWindow.WindowHandle);
        picker.FileTypeFilter.Add("*");
        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        await RunTransferAsync($"下载到 {folder.Path}", async (progress, setTotal, cancellation) =>
        {
            var files = new List<(SftpEntry Entry, string Local)>();
            foreach (var entry in selected) await CollectDownload(client, entry, Path.Combine(folder.Path, SafeName(entry.Name)), files);
            setTotal(files.Sum(f => f.Entry.Size));

            long done = 0;
            foreach (var (entry, local) in files)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                // Written beside the target so a cancelled transfer never leaves a truncated file behind.
                string partial = local + ".part";
                try
                {
                    await using (var stream = File.Create(partial))
                    {
                        long start = done;
                        await client.DownloadAsync(entry.FullPath, stream, entry.Size, new Progress<long>(v => progress.Report(start + v)), cancellation);
                    }
                    File.Move(partial, local, true);
                }
                catch
                {
                    File.Delete(partial);
                    throw;
                }
                done += entry.Size;
            }
        });
    }

    /// <summary>Remote names can contain characters Windows rejects, and must never escape the target folder.</summary>
    private static string SafeName(string name)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars()) name = name.Replace(invalid, '_');
        return name is "." or ".." ? "_" : name;
    }

    private static async Task CollectDownload(SftpClient client, SftpEntry entry, string local, List<(SftpEntry, string)> files)
    {
        if (!entry.IsDirectory)
        {
            files.Add((entry, local));
            return;
        }
        Directory.CreateDirectory(local);
        foreach (var child in await client.ListAsync(entry.FullPath))
            await CollectDownload(client, child, Path.Combine(local, SafeName(child.Name)), files);
    }

    private async Task<string?> AskNameAsync(string title, string initial)
    {
        var box = new TextBox { Text = initial, MinWidth = 340 };
        box.SelectAll();
        var dialog = new ContentDialog { Title = title, Content = box, PrimaryButtonText = "确定", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        var result = await Dialogs.ShowAsync(dialog);
        string name = box.Text.Trim();
        if (result != ContentDialogResult.Primary || name.Length == 0) return null;
        if (name.Contains('/'))
        {
            ShowError("名称无效", "名称不能包含 /");
            return null;
        }
        return name;
    }

    private async Task MakeDirectoryAsync()
    {
        var client = _client;
        if (client == null || await AskNameAsync("新建文件夹", "") is not { } name) return;
        try
        {
            await client.MakeDirectoryAsync(Join(_current, name));
            await NavigateAsync(_current);
        }
        catch (Exception ex)
        {
            HandleFailure("无法新建文件夹", ex);
        }
    }

    private async Task RenameSelectedAsync()
    {
        var client = _client;
        if (client == null || _list.SelectedItem is not SftpEntry entry || await AskNameAsync("重命名", entry.Name) is not { } name) return;
        try
        {
            await client.RenameAsync(entry.FullPath, Join(_current, name));
            await NavigateAsync(_current);
        }
        catch (Exception ex)
        {
            HandleFailure("无法重命名", ex);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        var client = _client;
        var selected = Selection;
        if (client == null || selected.Count == 0) return;
        string description = selected.Count == 1 ? selected[0].Name : $"{selected.Count} 个项目";
        bool hasDirectory = selected.Any(s => s.IsDirectory && !s.IsSymlink);
        string warning = hasDirectory ? "文件夹中的所有内容都会被永久删除，无法恢复。" : "删除后无法恢复。";
        if (!await Dialogs.ConfirmAsync($"删除 {description}？", warning, "删除", destructive: true)) return;
        try
        {
            foreach (var entry in selected) await client.RemoveRecursiveAsync(entry);
        }
        catch (Exception ex)
        {
            HandleFailure("删除失败", ex);
        }
        await NavigateAsync(_current);
    }
}
