using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VideoMarkdown.App;

internal sealed record CollectionResult(IReadOnlyCollection<Work> Works, string Status, bool Complete);

internal sealed class BrowserCollector(WebView2 browser, HttpClient http)
{
    private readonly Dictionary<string, string> requests = [];
    private CoreWebView2? web;
    private Collection? collection;
    private Platform platform;
    private string author = "";
    private string lastIssue = "";
    private bool collecting;
    private int generation;

    public static string ProfileDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoMarkdown", "WebView2");

    public async Task<CollectionResult> Collect(Platform source, Uri page, string authorId, DateOnly from,
        DateOnly through, Action<string> changed, CancellationToken token)
    {
        if (source == Platform.YouTube)
        {
            await Initialize();
            return await new YouTubeCollector(web!, http).Collect(page, from, through, changed, token);
        }
        var result = new Collection(source, from, through);
        var status = await CollectPlatform(source, page, authorId, result, changed, token);
        return new CollectionResult(result.WorksFound, status, result.Complete);
    }

    public async Task Initialize()
    {
        if (web is not null) return;
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: ProfileDirectory);
        await browser.EnsureCoreWebView2Async(environment);
        var core = browser.CoreWebView2;
        core.Settings.IsWebMessageEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.NavigationStarting += (_, args) => args.Cancel = !AllowedNavigation(args.Uri);
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            if (args.IsUserInitiated && AllowedNavigation(args.Uri)) core.Navigate(args.Uri);
        };
        core.DownloadStarting += (_, args) => args.Cancel = true;
        core.GetDevToolsProtocolEventReceiver("Network.responseReceived").DevToolsProtocolEventReceived += Receive;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFinished").DevToolsProtocolEventReceived += Finish;
        core.GetDevToolsProtocolEventReceiver("Network.loadingFailed").DevToolsProtocolEventReceived += (_, args) =>
        {
            using var payload = JsonDocument.Parse(args.ParameterObjectAsJson);
            requests.Remove(payload.RootElement.Get("requestId").StringValue());
        };
        await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        web = core;
    }

    private async Task<string> CollectPlatform(Platform source, Uri page, string authorId, Collection result,
        Action<string> changed, CancellationToken token)
    {
        await Initialize();
        token.ThrowIfCancellationRequested();
        platform = source;
        author = authorId;
        collection = result;
        collecting = true;
        requests.Clear();
        lastIssue = "";
        generation++;
        try
        {
            web!.Navigate(page.AbsoluteUri);
            if (!await WaitForPage(0, TimeSpan.FromSeconds(24), token))
                return Incomplete("未收到主页作品数据");

            var noProgress = 0;
            while (!result.Complete && !result.Exhausted && result.Pages < 300)
            {
                token.ThrowIfCancellationRequested();
                changed(Summary(result, "正在采集"));
                var previous = result.Pages;
                await Task.Delay(850, token);
                if (result.Complete) break;
                if (result.Pages != previous) continue;
                if (source == Platform.Bilibili)
                    await web.ExecuteScriptAsync(NextBiliPage);
                else
                    await web.ExecuteScriptAsync(ScrollDouyin);
                if (await WaitForPage(previous, TimeSpan.FromSeconds(5), token)) noProgress = 0;
                else if (++noProgress == 3) return Incomplete("未能继续翻页");
            }
            if (result.Complete) return Summary(result, "采集完成");
            return Incomplete(result.Exhausted ? "平台列表缺少日期或翻页信息" : "已达到本次翻页上限");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Incomplete("已停止采集");
        }
        finally
        {
            collecting = false;
            generation++;
            requests.Clear();
            collection = null;
        }

        string Incomplete(string reason) => Summary(result,
            $"采集未完整 · {reason}{(lastIssue.Length > 0 ? $" · {lastIssue}" : "")}。可在平台页面登录或验证后重试，或导入作品清单。");
    }

    public async Task<IReadOnlyDictionary<string, string>> Credentials(Platform source)
    {
        var credentials = new Dictionary<string, string>(StringComparer.Ordinal);
        if (source == Platform.YouTube) return credentials;
        if (web is null && Directory.Exists(ProfileDirectory)) await Initialize();
        if (web is null) return credentials;
        var cookies = web.CookieManager;
        if (source == Platform.Bilibili)
        {
            var names = new Dictionary<string, string> { ["SESSDATA"] = "sessdata", ["bili_jct"] = "bili_jct", ["buvid3"] = "buvid3" };
            foreach (var cookie in await cookies.GetCookiesAsync("https://www.bilibili.com/"))
                if (names.TryGetValue(cookie.Name, out var key)) credentials[key] = cookie.Value;
            if (!credentials.ContainsKey("sessdata")) credentials.Clear();
        }
        else
        {
            string[] names = ["msToken", "ttwid", "odin_tt", "passport_csrf_token", "sid_guard", "sessionid",
                "sessionid_ss", "s_v_web_id", "__ac_nonce", "__ac_signature", "tt_scid", "csrf_session_id"];
            foreach (var cookie in await cookies.GetCookiesAsync("https://www.douyin.com/"))
                if (names.Contains(cookie.Name, StringComparer.Ordinal)) credentials[cookie.Name] = cookie.Value;
            if (!credentials.ContainsKey("sessionid") && !credentials.ContainsKey("sid_guard")) credentials.Clear();
        }
        return credentials;
    }

    private void Receive(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!collecting || collection is null || collection.Complete) return;
        using var eventData = JsonDocument.Parse(args.ParameterObjectAsJson);
        var data = eventData.RootElement;
        var response = data.Get("response");
        if (!Uri.TryCreate(response.Get("url").StringValue(), UriKind.Absolute, out var url)) return;
        var path = url.AbsolutePath.TrimEnd('/');
        string cursor;
        if (platform == Platform.Bilibili && url.Host == "api.bilibili.com" && path == "/x/space/wbi/arc/search"
            && Works.Query(url, "mid") == author && Works.Query(url, "order") is "" or "pubdate" && Works.Query(url, "keyword") == ""
            && Works.Query(url, "tid") is "" or "0")
            cursor = Works.Query(url, "pn");
        else if (platform == Platform.Douyin && Works.IsDouyin(url.Host) && path == "/aweme/v1/web/aweme/post"
            && Works.Query(url, "sec_user_id") == author)
            cursor = Works.Query(url, "max_cursor");
        else return;
        var status = response.Get("status").Number();
        if (status is < 200 or >= 300)
        {
            lastIssue = $"平台返回 HTTP {status}";
            return;
        }
        requests[data.Get("requestId").StringValue()] = cursor.Length == 0 ? (platform == Platform.Bilibili ? "1" : "0") : cursor;
    }

    private async void Finish(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var eventData = JsonDocument.Parse(args.ParameterObjectAsJson);
        var id = eventData.RootElement.Get("requestId").StringValue();
        if (!collecting || !requests.Remove(id, out var cursor) || collection is not { } result || result.Complete) return;
        var current = generation;
        try
        {
            var body = await web!.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = id }));
            if (current != generation || result.Complete) return;
            using var document = JsonDocument.Parse(body);
            var text = document.RootElement.Get("body").StringValue();
            if (document.RootElement.Get("base64Encoded").Flag()) text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            if (!result.Accept(text, cursor)) lastIssue = "平台响应未能确认列表进度";
            else lastIssue = "";
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (current == generation) lastIssue = "平台返回了无法读取的作品数据";
        }
    }

    private async Task<bool> WaitForPage(int previous, TimeSpan maximum, CancellationToken token)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < maximum)
        {
            if (collection!.Pages > previous) return true;
            await Task.Delay(250, token);
        }
        return collection!.Pages > previous;
    }

    private static bool AllowedNavigation(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort
        && (Works.IsBili(uri.Host) || Works.IsDouyin(uri.Host) || Works.IsYouTube(uri.Host)
            || uri.Host is "consent.youtube.com");

    internal static string Summary(Collection result, string status)
    {
        var range = result.Oldest is null ? "尚无可用日期" : $"{result.Oldest:yyyy-MM-dd} 至 {result.Newest:yyyy-MM-dd}";
        return $"{status} · {result.WorksFound.Count} 条范围内视频 · 已读 {result.Pages} 页 · {range}";
    }

    private const string NextBiliPage = """
        (() => {
          const candidates = [...document.querySelectorAll('button, a, [role="button"], .vui_pagenation--btn')];
          const next = candidates.find(e => e.getClientRects().length &&
            (e.getAttribute('aria-label') === '下一页' || e.getAttribute('title') === '下一页' ||
             e.textContent.trim() === '下一页') &&
            !e.disabled && e.getAttribute('aria-disabled') !== 'true' &&
            !String(e.className).includes('disabled'));
          if (next) next.click();
        })()
        """;

    private const string ScrollDouyin = """
        (() => {
          const last = [...document.querySelectorAll('a[href*="/video/"], a[href*="/note/"]')]
            .filter(e => e.getClientRects().length).at(-1);
          if (last) last.scrollIntoView({ block: 'end' });
          const scrollers = [document.scrollingElement];
          for (let el = last?.parentElement; el; el = el.parentElement) {
            if (el.scrollHeight > el.clientHeight + 10 && /auto|scroll/.test(getComputedStyle(el).overflowY))
              scrollers.push(el);
          }
          for (const el of scrollers) if (el) el.scrollTop = el.scrollHeight;
          const more = [...document.querySelectorAll('button, [role="button"]')].find(e =>
            e.getClientRects().length && /^(加载更多|查看更多作品)$/.test(e.textContent.trim()));
          if (more && !more.disabled) more.click();
        })()
        """;
}
