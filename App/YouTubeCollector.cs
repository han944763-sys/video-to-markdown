using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace VideoMarkdown.App;

internal sealed class YouTubeCollector(CoreWebView2 web, HttpClient http)
{
    private const int PageLimit = 25;
    private readonly HashSet<string> read = [];
    private readonly Dictionary<string, Work> results = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim metadataSlots = new(4);
    private readonly Dictionary<string, DateOnly?> dateCache = new(StringComparer.Ordinal);
    private JsonElement client;
    private int pages;
    private int unknownDates;
    private DateOnly? oldest;
    private DateOnly? newest;

    public async Task<CollectionResult> Collect(Uri profile, DateOnly from, DateOnly through, Action<string> update, CancellationToken token)
    {
        try
        {
            var root = profile.AbsoluteUri[..(profile.AbsoluteUri.LastIndexOf('/') + 1)];
            var firstTab = profile.Segments[^1] == "shorts" ? "shorts" : "videos";
            var first = await OpenTab(profile, token);
            if (first is null) return Outcome("未读取到频道，检查平台页面或导入作品清单。");
            if (first.Available.Count == 0) return Outcome("频道未提供「视频」或「Shorts」列表。");

            var missing = new List<string>();
            foreach (var tab in new[] { firstTab, firstTab == "videos" ? "shorts" : "videos" })
            {
                if (!first.Available.Contains(tab)) continue;
                var snapshot = first.Name == tab && tab == firstTab ? first : await OpenTab(new Uri(root + tab), token);
                var label = tab == "videos" ? "视频" : "Shorts";
                if (snapshot?.Name != tab || snapshot.Page is null)
                {
                    missing.Add($"{label}列表未读取");
                    continue;
                }
                var issue = await Scan(snapshot.Page, new Uri(root + tab), label, from, through, update, token);
                if (issue.Length > 0) missing.Add(issue);
            }
            if (unknownDates > 0) missing.Add($"{unknownDates} 条日期待确认");
            return missing.Count == 0
                ? new CollectionResult(results.Values, Summary("采集完成"), true)
                : Outcome(string.Join(" · ", missing));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return Outcome("已停止采集"); }
        catch (Exception error) when (error is JsonException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            return Outcome("频道页面未返回可读取的列表");
        }
    }

    private async Task<YouTubeTab?> OpenTab(Uri page, CancellationToken token)
    {
        client = default;
        var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e) => navigationId ??= e.NavigationId;
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.NavigationId == navigationId) loaded.TrySetResult(e.IsSuccess);
        }
        web.NavigationStarting += Starting;
        web.NavigationCompleted += Completed;
        try
        {
            web.Navigate(page.AbsoluteUri);
            if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(24), token)) return null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                token.ThrowIfCancellationRequested();
                using var json = JsonDocument.Parse(await web.ExecuteScriptAsync("window.ytInitialData ?? null"));
                var tab = YouTubeListing.Initial(json.RootElement);
                if (tab is not null)
                {
                    using var config = JsonDocument.Parse(await web.ExecuteScriptAsync(ReadClient));
                    client = config.RootElement.Clone();
                    return tab;
                }
                await Task.Delay(400, token);
            }
            return null;
        }
        catch (TimeoutException) { return null; }
        finally
        {
            web.NavigationStarting -= Starting;
            web.NavigationCompleted -= Completed;
        }
    }

    private async Task<string> Scan(YouTubePage page, Uri tab, string label, DateOnly from, DateOnly through,
        Action<string> update, CancellationToken token)
    {
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var unrecognized = false;
        for (var index = 0; index < PageLimit; index++)
        {
            pages++;
            unrecognized |= page.UnrecognizedItems;
            update(Summary($"正在核对 YouTube {label}日期"));
            var entirelyOlder = page.Videos.Count > 0;
            foreach (var group in page.Videos.DistinctBy(entry => entry.Id).Chunk(12))
            {
                var dates = await Task.WhenAll(group.Select(entry => ReadDate(entry, token)));
                foreach (var (entry, date) in dates)
                {
                    if (date is null || date >= from) entirelyOlder = false;
                    if (!read.Add(entry.Id)) continue;
                    if (date is null) unknownDates++;
                    else
                    {
                        if (oldest is null || date < oldest) oldest = date;
                        if (newest is null || date > newest) newest = date;
                        if (date < from || date > through) continue;
                    }
                    var url = new Uri($"https://www.youtube.com/watch?v={entry.Id}");
                    results.TryAdd(entry.Id, Works.FromUrl(Platform.YouTube, url, date, entry.Title));
                }
                update(Summary($"正在核对 YouTube {label}日期"));
            }
            if (page.Next.Length == 0 || (index > 0 && entirelyOlder && !page.UnrecognizedItems))
                return unrecognized ? $"{label}列表有无法识别的条目" : "";
            if (!cursors.Add(page.Next)) return $"{label}翻页重复";
            if (index == PageLimit - 1) break;
            var next = await NextPage(tab, page.Next, token);
            if (next is null) return $"{label}翻页未返回数据";
            page = next;
        }
        return $"{label}达到 {PageLimit} 页采集上限";
    }

    private async Task<(YouTubeEntry Entry, DateOnly? Date)> ReadDate(YouTubeEntry entry, CancellationToken token)
    {
        if (dateCache.TryGetValue(entry.Id, out var cached)) return (entry, cached);
        await metadataSlots.WaitAsync(token);
        try
        {
            using var response = await http.GetAsync($"https://www.youtube.com/watch?v={entry.Id}", token);
            var date = response.IsSuccessStatusCode
                ? YouTubeListing.PublishedDate(await response.Content.ReadAsStringAsync(token)) : null;
            dateCache[entry.Id] = date;
            return (entry, date);
        }
        catch (HttpRequestException) { return (entry, null); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return (entry, null); }
        finally { metadataSlots.Release(); }
    }

    private async Task<YouTubePage?> NextPage(Uri tab, string cursor, CancellationToken token)
    {
        if (client.Get("clientVersion").StringValue().Length == 0) return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://www.youtube.com/youtubei/v1/browse?prettyPrint=false");
            request.Headers.Referrer = tab;
            request.Headers.Add("Origin", "https://www.youtube.com");
            request.Content = JsonContent.Create(new { context = new { client }, continuation = cursor });
            using var response = await http.SendAsync(request, token);
            if (!response.IsSuccessStatusCode) return null;
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            return YouTubeListing.Continuation(data.RootElement);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
    }

    private string Summary(string text) =>
        $"{text} · {results.Count - unknownDates} 条范围内视频 · 已读 {pages} 页 · " +
        (oldest is null ? "尚无已确认日期" : $"{oldest:yyyy-MM-dd} 至 {newest:yyyy-MM-dd}");

    private CollectionResult Outcome(string reason) => new(results.Values, Summary($"采集未完整 · {reason}"), false);

    private const string ReadClient = """
        (() => {
          const c = window.ytcfg?.get('INNERTUBE_CONTEXT')?.client;
          return c ? {clientName: c.clientName, clientVersion: c.clientVersion, hl: c.hl, gl: c.gl} : null;
        })()
        """;
}
