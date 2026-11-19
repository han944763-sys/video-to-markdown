using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace VideoMarkdown.App;

internal sealed class BrowserComments(CoreWebView2 web, HttpClient http)
{
    private readonly HashSet<string> requests = [];
    private readonly HashSet<string> videoRequests = [];
    private readonly CommentReader reader = new();
    private bool active;
    private string issue = "";
    private string biliId = "";
    private Work? work;

    public async Task<CommentSnapshot> Read(Work source, Action<string> update, CancellationToken token)
    {
        work = source;
        if (source.Platform == Platform.Bilibili)
        {
            try
            {
                using var response = await http.GetAsync($"https://api.bilibili.com/x/web-interface/view?bvid={source.Id}", token);
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                biliId = body.RootElement.Get("data").Get("aid").StringValue();
            }
            catch (Exception error) when (error is HttpRequestException or JsonException
                || error is OperationCanceledException && !token.IsCancellationRequested) { }
        }
        var received = web.GetDevToolsProtocolEventReceiver("Network.responseReceived");
        var finished = web.GetDevToolsProtocolEventReceiver("Network.loadingFinished");
        var started = web.GetDevToolsProtocolEventReceiver("Network.requestWillBeSent");
        started.DevToolsProtocolEventReceived += Started;
        received.DevToolsProtocolEventReceived += Received;
        finished.DevToolsProtocolEventReceived += Finished;
        active = true;
        try
        {
            web.Navigate(source.Url);
            var watch = Stopwatch.StartNew();
            var lastChange = TimeSpan.Zero;
            var count = 0;
            while (watch.Elapsed < TimeSpan.FromSeconds(60) && !reader.AtLimit)
            {
                await Task.Delay(1000, token);
                token.ThrowIfCancellationRequested();
                if (watch.Elapsed > TimeSpan.FromSeconds(8) && !IsVideo(web.Source, source))
                {
                    issue = "平台页面已跳转，评论尚未收齐。";
                    break;
                }
                await web.ExecuteScriptAsync(ScrollComments);
                if (reader.Count != count)
                {
                    count = reader.Count;
                    lastChange = watch.Elapsed;
                    update($"正在保存评论 · {source.DisplayTitle} · {count} 条");
                }
                if (reader.EndReached && watch.Elapsed - lastChange > TimeSpan.FromSeconds(2)) break;
                if (reader.Received && watch.Elapsed - lastChange > TimeSpan.FromSeconds(8)) break;
            }
            return reader.Snapshot(issue);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return reader.Snapshot("平台浏览器连接中断，可重试补存评论。");
        }
        finally
        {
            active = false;
            started.DevToolsProtocolEventReceived -= Started;
            received.DevToolsProtocolEventReceived -= Received;
            finished.DevToolsProtocolEventReceived -= Finished;
        }
    }

    private static bool IsVideo(string url, Work source)
    {
        try { return Works.FromUrl(source.Platform, new Uri(url), null, "").Id == source.Id; }
        catch (FormatException) { return false; }
    }

    private void Started(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!active || work?.Platform != Platform.YouTube) return;
        using var data = JsonDocument.Parse(args.ParameterObjectAsJson);
        if (IsVideo(data.RootElement.Get("documentURL").StringValue(), work))
            videoRequests.Add(data.RootElement.Get("requestId").StringValue());
    }

    private void Received(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        if (!active || work is null) return;
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var data = document.RootElement;
        var response = data.Get("response");
        if (!Uri.TryCreate(response.Get("url").StringValue(), UriKind.Absolute, out var url)) return;
        var path = url.AbsolutePath.TrimEnd('/');
        var matches = work.Platform switch
        {
            Platform.Bilibili => url.Host == "api.bilibili.com"
                && path is "/x/v2/reply/wbi/main" or "/x/v2/reply/main" or "/x/v2/reply"
                && biliId.Length > 0 && Works.Query(url, "oid") == biliId,
            Platform.Douyin => Works.IsDouyin(url.Host) && path == "/aweme/v1/web/comment/list"
                && Works.Query(url, "aweme_id") == work.Id,
            Platform.YouTube => Works.IsYouTube(url.Host) && path == "/youtubei/v1/next"
                && videoRequests.Remove(data.Get("requestId").StringValue()),
            _ => false
        };
        if (!matches) return;
        if (response.Get("status").Number() is < 200 or >= 300)
        {
            issue = $"评论接口返回 HTTP {response.Get("status").StringValue()}，可登录或验证后重试。";
            return;
        }
        requests.Add(data.Get("requestId").StringValue());
    }

    private async void Finished(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs args)
    {
        using var document = JsonDocument.Parse(args.ParameterObjectAsJson);
        var id = document.RootElement.Get("requestId").StringValue();
        if (!active || !requests.Remove(id) || work is null) return;
        try
        {
            var body = await web.CallDevToolsProtocolMethodAsync("Network.getResponseBody", JsonSerializer.Serialize(new { requestId = id }));
            if (!active) return;
            using var data = JsonDocument.Parse(body);
            var text = data.RootElement.Get("body").StringValue();
            if (data.RootElement.Get("base64Encoded").Flag()) text = Encoding.UTF8.GetString(Convert.FromBase64String(text));
            reader.Accept(work.Platform, text);
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            if (active) issue = "部分评论数据未能读取，可重试补存。";
        }
    }

    private const string ScrollComments = """
        (() => {
          document.querySelectorAll('video, audio').forEach(v => { v.muted = true; v.pause(); });
          const roots = [document];
          function collect(root) {
            for (const el of root.querySelectorAll('*')) {
              if (el.shadowRoot) { roots.push(el.shadowRoot); collect(el.shadowRoot); }
            }
          }
          collect(document);
          const all = selector => roots.flatMap(r => [...r.querySelectorAll(selector)]);
          if (!document.querySelector('[data-e2e="comment-list"]')) {
            document.querySelector('[data-e2e="comment-icon"]')?.click();
          }
          const comments = all('ytd-comment-thread-renderer, .reply-item, bili-comment-thread-renderer, [data-e2e="comment-item"]')
            .filter(e => e.getClientRects().length);
          const last = comments.at(-1);
          const panel = document.querySelector('ytd-comments, [data-e2e="comment-list"]') ||
            document.querySelector('bili-comments') || all('#comment, .reply-list')[0];
          const next = document.querySelector('ytd-comments ytd-continuation-item-renderer');
          if (next) next.scrollIntoView({block:'center'});
          else if (last) { last.scrollIntoView({block:'end'}); window.scrollBy(0, 300); }
          else if (panel) panel.scrollIntoView({block:'start'});
          else window.scrollBy(0, 600);
          const scrollers = [];
          for (let el = last || panel; el; el = el.parentElement) {
            if (el.scrollHeight > el.clientHeight + 10 && /auto|scroll/.test(getComputedStyle(el).overflowY))
              scrollers.push(el);
          }
          scrollers.forEach(el => el.scrollTop += Math.max(400, el.clientHeight));
          for (const el of all('ytd-comments #more-replies button, ytd-comments #expander button')) {
            if (el.getClientRects().length && !el.dataset.videoMarkdownOpened) {
              el.dataset.videoMarkdownOpened = '1'; el.click(); break;
            }
          }
        })()
        """;
}
