using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace VideoMarkdown.App;

public partial class MainWindow : Window
{
    private readonly HttpClient platformHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(12)
    };
    private readonly HttpClient localHttp = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
    {
        BaseAddress = new Uri("http://127.0.0.1:8000/"), Timeout = TimeSpan.FromSeconds(10)
    };
    private readonly BrowserCollector collector;
    private readonly VideoToNo service;
    private readonly CancellationTokenSource lifetime = new();
    private Batch batch = new();
    private BatchFiles? files;
    private FileStream? batchLock;
    private CancellationTokenSource? collectionCancellation;
    private bool busy;
    private bool running;
    private bool pause;
    private string? executable;

    public MainWindow()
    {
        InitializeComponent();
        platformHttp.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/149.0.0.0 Safari/537.36");
        collector = new BrowserCollector(PlatformView, platformHttp);
        service = new VideoToNo(localHttp);
        executable = @"D:\Tools\VideoToNo\VideoToNo-1.4.7-portable.exe";
        FillForm();
        Loaded += async (_, _) => await UpdateHealth();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            lifetime.Cancel();
            batchLock?.Dispose();
            platformHttp.Dispose();
            localHttp.Dispose();
            PlatformView.Dispose();
        };
    }

    private Platform PlatformChoice => (Platform)Math.Max(0, PlatformBox.SelectedIndex);

    private void FillForm()
    {
        PlatformBox.SelectedIndex = (int)batch.Platform;
        ProfileBox.Text = batch.Profile;
        FromPicker.SelectedDate = batch.From.ToDateTime(TimeOnly.MinValue);
        ThroughPicker.SelectedDate = batch.Through.ToDateTime(TimeOnly.MinValue);
        OutputBox.Text = files?.DirectoryPath ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "视频转 Markdown");
        WorkGrid.ItemsSource = batch.Items;
        Refresh();
    }

    private void Refresh()
    {
        WorkGrid.Items.Refresh();
        Totals.Text = $"共 {batch.Items.Count} 条 · 已选 {batch.Items.Count(w => w.Selected)} · 已导出 {batch.Items.Count(w => w.Status == WorkStatus.Succeeded)}";
        ContinueButton.IsEnabled = !busy && batch.Items.Any(w => w.Status == WorkStatus.Running
            || (w.Selected && w.Status is WorkStatus.Ready or WorkStatus.ExportFailed));
        RetryButton.IsEnabled = !busy && batch.Items.Any(w => w.Status is WorkStatus.Failed or WorkStatus.ExportFailed or WorkStatus.SubmissionUnknown);
        StopCollect.IsEnabled = collectionCancellation is not null;
        PauseButton.IsEnabled = running && !pause;
        SourceForm.IsEnabled = !busy;
        ImportForm.IsEnabled = !busy;
        WorkGrid.IsEnabled = !busy;
        SaveButton.IsEnabled = !busy && batch.Items.Count > 0;
        PlatformBox.IsEnabled = files is null && batch.Items.Count == 0;
        SearchButton.Visibility = PlatformChoice == Platform.Douyin ? Visibility.Visible : Visibility.Collapsed;
        OutputBox.IsReadOnly = files is not null;
        ProfileBox.IsReadOnly = files is not null && batch.Profile.Length > 0;
        FromPicker.IsEnabled = files is null;
        ThroughPicker.IsEnabled = files is null;
    }

    private void PlatformChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SearchButton is null) return;
        SearchButton.Visibility = PlatformChoice == Platform.Douyin ? Visibility.Visible : Visibility.Collapsed;
        if (files is null && batch.Platform != PlatformChoice)
        {
            batch.Platform = PlatformChoice;
            batch.Profile = "";
            batch.CollectionStatus = "";
            ProfileBox?.Clear();
        }
    }

    private (DateOnly From, DateOnly Through) Dates()
    {
        if (FromPicker.SelectedDate is not { } start || ThroughPicker.SelectedDate is not { } end || start > end)
            throw new FormatException("请选择有效的开始和结束日期。");
        return (DateOnly.FromDateTime(start), DateOnly.FromDateTime(end));
    }

    private void UpdateDraft()
    {
        var dates = Dates();
        batch.Platform = PlatformChoice;
        batch.From = dates.From;
        batch.Through = dates.Through;
    }

    private void UpdateStatus(string message)
    {
        StatusLine.Text = message;
        Refresh();
    }

    private async Task Guard(Func<Task> work, bool isRunning = false)
    {
        if (busy) return;
        busy = true;
        running = isRunning;
        pause = false;
        Refresh();
        try { await work(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or FormatException or ArgumentException
            or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException
            or System.Runtime.InteropServices.COMException)
        {
            StatusLine.Text = $"操作未完成 · {VideoToNo.SafeError(error.Message)}";
        }
        finally
        {
            busy = running = false;
            if (!lifetime.IsCancellationRequested) Refresh();
        }
    }

    private async void Collect(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        await Guard(async () =>
        {
            UpdateDraft();
            var entered = Works.ParseUrl(ProfileBox.Text);
            var resolved = await Works.ResolveShortAsync(platformHttp, batch.Platform, entered, lifetime.Token);
            var profile = Works.Profile(batch.Platform, resolved);
            if (files is not null && batch.Profile.Length > 0 && !SameProfile(profile.Page))
                throw new FormatException("此批次已有作者主页。可新建批次采集另一位作者。");
            batch.Profile = ProfileBox.Text = profile.Page.AbsoluteUri;
            Tabs.SelectedItem = BrowserTab;
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            collectionCancellation = operation;
            UpdateStatus("正在打开作者主页…");
            try
            {
                var result = await collector.Collect(batch.Platform, profile.Page, profile.Id, batch.From, batch.Through,
                    UpdateStatus, operation.Token);
                if (lifetime.IsCancellationRequested) return;
                Merge(result.Works);
                batch.CollectionStatus = result.Status;
                if (files is not null) files.Save(batch);
                if (result.Complete) Tabs.SelectedItem = ListTab;
                UpdateStatus(result.Status);
            }
            finally { collectionCancellation = null; }
        });
    }

    private async void SearchDouyin(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var query = ProfileBox.Text.Trim();
        if (query.Length == 0 || query.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            throw new FormatException("可在作者主页栏输入抖音号或昵称，再点「查找抖音作者」。");
        Tabs.SelectedItem = BrowserTab;
        await collector.Initialize();
        PlatformView.CoreWebView2.Navigate($"https://www.douyin.com/search/{Uri.EscapeDataString(query)}?type=user");
        UpdateStatus("在抖音页面核对账号并打开作者主页，再点「使用当前主页」。");
    });

    private async void OpenPlatform(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await collector.Initialize();
        PlatformView.CoreWebView2.Navigate(PlatformChoice switch
        {
            Platform.Bilibili => "https://www.bilibili.com/",
            Platform.Douyin => "https://www.douyin.com/",
            _ => "https://www.youtube.com/"
        });
        UpdateStatus("可在平台页面登录或验证。完成后回到作品列表重试失败项。");
    });

    private async void UseCurrentProfile(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (PlatformView.Source is not { } source)
            throw new FormatException("平台页面还没有打开作者主页。");
        var profile = Works.Profile(PlatformChoice, source);
        if (files is not null && batch.Profile.Length > 0 && !SameProfile(profile.Page))
            throw new FormatException("此批次已有作者主页。可新建批次采集另一位作者。");
        ProfileBox.Text = profile.Page.AbsoluteUri;
        UpdateStatus("已填入当前作者主页，可以开始采集。");
        return Task.CompletedTask;
    });

    private bool SameProfile(Uri page)
    {
        if (batch.Profile == page.AbsoluteUri) return true;
        return PlatformChoice == Platform.YouTube
            && Works.Profile(Platform.YouTube, new Uri(batch.Profile)).Id.Equals(
                Works.Profile(Platform.YouTube, page).Id, StringComparison.OrdinalIgnoreCase);
    }

    private void CancelCollect(object sender, RoutedEventArgs e) => collectionCancellation?.Cancel();

    private async void ImportLinks(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var rows = LinksBox.Text.Split('\n').SelectMany((line, index) => Works.Links().Matches(line)
            .Select(match => new ImportRow(index + 1, match.Value, null, ""))).ToList();
        if (rows.Count == 0) throw new FormatException("请粘贴至少一条作品链接。");
        var errors = LinksBox.Text.Split('\n').Select((line, index) => (line, index))
            .Where(x => x.line.Trim().Length > 0 && !Works.Links().IsMatch(x.line))
            .Select(x => $"第 {x.index + 1} 行：未找到链接。").ToList();
        await Import(rows, errors);
    });

    private async void ImportCsv(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        var picker = new OpenFileDialog { Filter = "UTF-8 CSV|*.csv", Title = "导入作品清单" };
        if (Directory.Exists(OutputBox.Text)) picker.InitialDirectory = OutputBox.Text;
        if (picker.ShowDialog(this) != true) return;
        using var reader = new StreamReader(picker.FileName, new UTF8Encoding(false, true), true);
        var (rows, errors) = Works.ReadCsv(reader);
        await Import(rows, errors);
    });

    private async Task Import(List<ImportRow> rows, List<string> errors)
    {
        UpdateDraft();
        var accepted = new List<Work>();
        var skipped = 0;
        foreach (var row in rows)
        {
            lifetime.Token.ThrowIfCancellationRequested();
            if (row.Published is { } date && (date < batch.From || date > batch.Through))
            {
                skipped++;
                continue;
            }
            try
            {
                var original = Works.ParseUrl(row.Url);
                var resolved = await Works.ResolveShortAsync(platformHttp, batch.Platform, original, lifetime.Token);
                var item = Works.FromUrl(batch.Platform, resolved, row.Published, row.Title);
                if (item.Platform == Platform.Bilibili && item.Id.StartsWith("av", StringComparison.Ordinal))
                {
                    using var response = await platformHttp.GetAsync(
                        $"https://api.bilibili.com/x/web-interface/view?aid={item.Id[2..]}", lifetime.Token);
                    response.EnsureSuccessStatusCode();
                    using var metadata = JsonDocument.Parse(await response.Content.ReadAsStringAsync(lifetime.Token));
                    var bvid = metadata.RootElement.Get("data").Get("bvid").StringValue();
                    if (bvid.Length == 0) throw new FormatException("无法确认 av 号对应的视频，请使用 BV 链接。");
                    item = Works.FromUrl(batch.Platform, new Uri($"https://www.bilibili.com/video/{bvid}"), row.Published, row.Title);
                }
                accepted.Add(item);
            }
            catch (Exception error) when (error is FormatException or HttpRequestException or JsonException
                || (error is OperationCanceledException && !lifetime.IsCancellationRequested))
            {
                errors.Add($"第 {row.Line} 行：{VideoToNo.SafeError(error.Message)}");
            }
        }
        var count = Merge(accepted);
        if (files is not null) files.Save(batch);
        UpdateStatus($"新增 {count} 条 · 重复 {accepted.Count - count} 条 · 日期范围外 {skipped} 条 · 无效 {errors.Count} 条");
        if (errors.Count > 0) ShowImportErrors(errors);
    }

    private void ShowImportErrors(List<string> errors)
    {
        var details = new TextBox
        {
            Text = string.Join(Environment.NewLine, errors), IsReadOnly = true,
            Margin = new Thickness(12), TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        new Window
        {
            Title = "导入问题", Owner = this, Width = 560, Height = 310,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = details
        }.ShowDialog();
    }

    private int Merge(IEnumerable<Work> incoming)
    {
        var byId = batch.Items.ToDictionary(work => work.Id, StringComparer.Ordinal);
        var count = 0;
        foreach (var work in incoming)
        {
            if (byId.TryAdd(work.Id, work))
            {
                batch.Items.Add(work);
                count++;
            }
            else
            {
                var existing = byId[work.Id];
                if (existing.Status == WorkStatus.Ready && existing.Published is null && work.Published.HasValue)
                {
                    existing.Published = work.Published;
                    existing.Selected = true;
                }
                if (existing.Status == WorkStatus.Ready && existing.Title.Length == 0) existing.Title = work.Title;
            }
        }
        if (count > 0) batch.Items.Sort((left, right) => Nullable.Compare(right.Published, left.Published));
        return count;
    }

    private void PickOutput(object sender, RoutedEventArgs e)
    {
        if (files is not null)
        {
            StatusLine.Text = "已保存批次使用原输出目录。新建批次可选择其他目录。";
            return;
        }
        var dialog = new OpenFolderDialog { Title = "选择批次的输出根目录" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) == true) OutputBox.Text = dialog.FolderName;
    }

    private async void ShowOutput(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var directory = files?.DirectoryPath ?? OutputBox.Text;
        if (!Directory.Exists(directory)) throw new IOException("输出目录尚未生成。");
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
        return Task.CompletedTask;
    });

    private async void NewBatch(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (!CanReplace()) return Task.CompletedTask;
        batchLock?.Dispose();
        batchLock = null;
        files = null;
        batch = new Batch { Platform = PlatformChoice };
        LinksBox.Clear();
        FillForm();
        Tabs.SelectedItem = ListTab;
        StatusLine.Text = "已新建批次。";
        return Task.CompletedTask;
    });

    private async void OpenBatch(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var dialog = new OpenFileDialog { Filter = "批次状态|批次状态.json", Title = "打开已保存的批次状态" };
        if (Directory.Exists(OutputBox.Text)) dialog.InitialDirectory = OutputBox.Text;
        if (dialog.ShowDialog(this) != true || !CanReplace()) return Task.CompletedTask;
        var candidate = new BatchFiles(Path.GetDirectoryName(dialog.FileName)!);
        if (files?.DirectoryPath.Equals(candidate.DirectoryPath, StringComparison.OrdinalIgnoreCase) == true)
            return Task.CompletedTask;
        var handle = Lock(candidate);
        try
        {
            var loaded = candidate.Load();
            candidate.Save(loaded);
            batchLock?.Dispose();
            batchLock = handle;
            batch = loaded;
            files = candidate;
        }
        catch { handle.Dispose(); throw; }
        FillForm();
        Tabs.SelectedItem = ListTab;
        StatusLine.Text = string.IsNullOrEmpty(batch.CollectionStatus) ? "已打开批次。点击「开始 / 继续」可恢复。" : batch.CollectionStatus;
        return Task.CompletedTask;
    });

    private bool CanReplace() => files is not null || batch.Items.Count == 0
        || MessageBox.Show(this, "当前作品列表尚未保存。是否放弃并切换批次？", "切换批次",
            MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    private static FileStream Lock(BatchFiles candidate)
    {
        Directory.CreateDirectory(candidate.DirectoryPath);
        try { return new FileStream(Path.Combine(candidate.DirectoryPath, ".batch.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error) { throw new IOException("该批次已在另一个窗口打开，或目录不可用。", error); }
    }

    private async void Start(object sender, RoutedEventArgs e) => await Guard(Run, true);

    private async void SaveBatch(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        EnsureSaved();
        UpdateStatus("已保存作品清单与批次状态。");
        return Task.CompletedTask;
    });

    private void EnsureSaved()
    {
        UpdateDraft();
        if (files is null)
        {
            if (batch.Items.Any(work => work.Selected && work.Published is { } date && (date < batch.From || date > batch.Through)))
                throw new FormatException("已选作品中有日期超出范围。调整日期或取消勾选这些作品。");
            if (!Path.IsPathFullyQualified(OutputBox.Text.Trim()))
                throw new FormatException("请选择一个完整的本地输出目录。");
            var name = $"批次_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}";
            var candidate = new BatchFiles(Path.Combine(OutputBox.Text.Trim(), name));
            var handle = Lock(candidate);
            try { candidate.Save(batch); }
            catch { handle.Dispose(); throw; }
            files = candidate;
            batchLock = handle;
            OutputBox.Text = files.DirectoryPath;
        }
        else files.Save(batch);
    }

    private async Task Run()
    {
        EnsureSaved();
        Tabs.SelectedItem = ListTab;
        await UpdateHealth();
        await new TranscriptionQueue(service, TimeSpan.FromSeconds(2))
            .Run(batch, files!, collector.Credentials, () => pause, UpdateStatus, lifetime.Token);
    }

    private void Pause(object sender, RoutedEventArgs e)
    {
        pause = true;
        UpdateStatus("会在当前条处理结束后暂停。");
    }

    private async void Retry(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        if (files is null) return;
        var unknown = batch.Items.Any(work => work.Status == WorkStatus.SubmissionUnknown);
        var retryUnknown = unknown && MessageBox.Show(this,
            "有作品未收到任务 ID。请先确认 VideoToNo 已停止处理这些作品。现在重新提交这些作品吗？",
            "确认重新提交", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
        foreach (var work in batch.Items)
        {
            if (work.Status == WorkStatus.Failed || (retryUnknown && work.Status == WorkStatus.SubmissionUnknown))
            {
                work.Status = WorkStatus.Ready;
                work.TaskId = null;
                work.Error = null;
                work.Selected = true;
            }
            if (work.Status == WorkStatus.ExportFailed) work.Selected = true;
        }
        files.Save(batch);
        await Run();
    }, true);

    private void ToggleWork(object sender, RoutedEventArgs e)
    {
        try
        {
            files?.Save(batch);
            Refresh();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            UpdateStatus($"保存选择失败 · {VideoToNo.SafeError(error.Message)}");
        }
    }

    private async Task UpdateHealth()
    {
        try
        {
            ServiceStatus.Text = await service.Healthy(lifetime.Token) ? "VideoToNo 已连接" : "VideoToNo 未连接";
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void LaunchService(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await UpdateHealth();
        if (ServiceStatus.Text == "VideoToNo 已连接") return;
        if (!File.Exists(executable))
        {
            var dialog = new OpenFileDialog { Filter = "VideoToNo|VideoToNo*.exe", Title = "选择已安装的 VideoToNo 1.4.7" };
            if (dialog.ShowDialog(this) != true) return;
            executable = dialog.FileName;
        }
        Process.Start(new ProcessStartInfo(executable!) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(executable)! });
        for (var attempt = 0; attempt < 15; attempt++)
        {
            await Task.Delay(1000, lifetime.Token);
            await UpdateHealth();
            if (ServiceStatus.Text == "VideoToNo 已连接") break;
        }
        StatusLine.Text = ServiceStatus.Text == "VideoToNo 已连接"
            ? "VideoToNo 可以处理转录任务。"
            : "未连到 VideoToNo 1.4.7，请检查托盘图标和本机 8000 端口。";
    });

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (busy && !running)
        {
            e.Cancel = true;
            UpdateStatus("当前操作结束后可关闭窗口。");
            return;
        }
        if (running && files is null)
        {
            e.Cancel = true;
            return;
        }
        if (!busy && !CanReplace()) e.Cancel = true;
    }
}
