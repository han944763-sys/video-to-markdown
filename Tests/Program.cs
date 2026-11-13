using System.Net;
using System.Text;
using System.Text.Json;
using VideoMarkdown;

var from = new DateOnly(2026, 6, 13);
var through = new DateOnly(2026, 11, 13);
const string id = "7567890123456789001";
const string id2 = "7567890123456789002";
var taskId = Guid.NewGuid().ToString();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = timeout.Token;
var scratch = Path.Combine(Path.GetTempPath(), $"VideoMarkdownTests-{Guid.NewGuid():N}");
Directory.CreateDirectory(scratch);

await TestSync("北京时间与含首尾的日期范围", () =>
{
    Check(Works.BeijingDate(Stamp("2026-06-12T16:00:00Z")) == from, "UTC 到北京时间跨日");
    var result = new Collection(Platform.Bilibili, from, through);
    Check(result.Accept(Bili(1, 3, 3,
        ("BV1abcdefgh1", Stamp("2026-11-13T15:59:59Z"), false),
        ("BV1abcdefgh2", Stamp("2026-06-12T16:00:00Z"), false),
        ("BV1abcdefgh3", Stamp("2026-06-12T15:59:59Z"), true)), "1"), "读取第一页");
    Check(result.Complete && result.WorksFound.Count == 2, "包含首尾，排除旧置顶");
});
await TestSync("置顶、缺页和翻页中断不会提前宣布完成", () =>
{
    var result = new Collection(Platform.Douyin, from, through);
    Check(result.Accept(Douyin(10, true, (id, Stamp("2026-05-05T00:00:00Z"), true)), "0"), "读取置顶页");
    Check(!result.Complete && !result.Accept("{\"status_code\":0}", "10"), "空响应不是列表结束");
    Check(!result.Accept(Douyin(40, false), "20"), "漏页被拒绝");
    Check(result.Accept(Douyin(20, true, (id2, Stamp("2026-06-13T00:00:00Z"), false)), "10"), "继续正常页");
    Check(!result.Complete && result.WorksFound.Single().Id == id2, "中断时保留已发现记录");
    Check(result.Accept(Douyin(30, true, (id, Stamp("2026-06-01T00:00:00Z"), false)), "20") && result.Complete, "实际翻过下界");
});
await Test("短链、分 P 归一化与长数字 ID 去重", async () =>
{
    using var client = new HttpClient(new Handler(req =>
        new HttpResponseMessage(HttpStatusCode.Found)
        {
            Headers = { Location = new Uri($"https://www.douyin.com/video/{id}?from=share") }
        }));
    var a = await Works.ResolveShortAsync(client, Platform.Douyin, new Uri("https://v.douyin.com/example/"), token);
    var direct = Video(id);
    Check(Works.FromUrl(Platform.Douyin, a, null, "").Url == direct.Url && direct.Id == id, "保留数字文本");
    var bili = Works.FromUrl(Platform.Bilibili, new Uri("https://www.bilibili.com/video/BV1abcdefgh1/?p=3"), null, "");
    Check(!bili.Url.Contains('?'), "清除分 P 参数，转整部作品");
});
await TestSync("UTF-8 CSV 引号、无日期以及无效行", () =>
{
    var (rows, errors) = Works.ReadCsv(new StringReader(
        $"url,published_date,title\r\n{Video(id).Url},,\"中国,视频\"\r\n{Video(id2).Url},2026-99-01,错\r\n{Video(id2).Url},2026-06-13,对"));
    Check(rows.Count == 2 && errors.Count == 1 && rows[0].Title == "中国,视频", "CSV 按行解析");
    Check(!Works.FromUrl(Platform.Douyin, new Uri(rows[0].Url), rows[0].Published, rows[0].Title).Selected, "未知日期不预选");
});
await Test("重启续跑复用任务 ID 和有效 Markdown", async () =>
{
    var work = Video(id);
    work.Status = WorkStatus.Running;
    work.TaskId = taskId;
    work.Selected = true;
    var (batch, files) = Save("resume", work);
    var submits = 0;
    using var http = Fake(req =>
    {
        if (req.Method == HttpMethod.Post) submits++;
        return Service(req);
    });
    batch = files.Load();
    await Run(http, batch, files);
    var item = batch.Items[0];
    Check(submits == 0 && item.Status == WorkStatus.Succeeded, "没有重新提交");
    Check(File.ReadAllText(Path.Combine(files.DirectoryPath, item.MarkdownPath!)).Contains("[00:00-00:03] 样本"), "有效时间戳");
    Check(files.Load().Items[0].Status == WorkStatus.Succeeded, "落盘状态");
});
await Test("空转录独立标记", async () =>
{
    var work = Video(id);
    work.Status = WorkStatus.Running;
    work.TaskId = taskId;
    var (batch, files) = Save("empty", work);
    using var http = Fake(req => req.RequestUri!.Query.Contains("json") ? Json("{\"segments\":[]}") : Service(req));
    await Run(http, batch, files);
    Check(work.Status == WorkStatus.NoTranscript && work.MarkdownPath is null, "空转录不导出");
});
await Test("写入失败保留原任务，恢复后直接导出", async () =>
{
    var work = Video(id);
    work.Status = WorkStatus.Running;
    work.TaskId = taskId;
    work.Selected = true;
    var (batch, files) = Save("export", work);
    var obstruct = Path.Combine(files.DirectoryPath, "转录稿");
    File.WriteAllText(obstruct, "测试占位");
    using var http = Fake(Service);
    await Run(http, batch, files);
    Check(work.Status == WorkStatus.ExportFailed && work.TaskId == taskId, "保留导出失败和任务 ID");
    File.Delete(obstruct);
    await Run(http, batch, files);
    Check(work.Status == WorkStatus.Succeeded && work.TaskId == taskId, "从原任务恢复");
});
await Test("提交结果未知不自动重试", async () =>
{
    var work = Video(id);
    work.Selected = true;
    var (batch, files) = Save("unknown", work);
    var submits = 0;
    using var http = Fake(req =>
    {
        if (req.Method == HttpMethod.Post)
        {
            submits++;
            throw new HttpRequestException("connection lost");
        }
        return Service(req);
    });
    await Run(http, batch, files);
    Check(work.Status == WorkStatus.SubmissionUnknown && work.TaskId is null, "未收到 ID");
    await Run(http, files.Load(), files);
    Check(submits == 1, "恢复不重复提交");
    work.Status = WorkStatus.Submitting;
    files.Save(batch);
    Check(files.Load().Items[0].Status == WorkStatus.SubmissionUnknown, "处理提交中进程退出");
});
await Test("当前条处理完后暂停", async () =>
{
    var first = Video(id);
    first.Selected = true;
    var second = Video(id2);
    second.Selected = true;
    var (batch, files) = Save("pause", first, second);
    var submissions = 0;
    using var http = Fake(req =>
    {
        if (req.Method == HttpMethod.Post) submissions++;
        return Service(req);
    });
    await new TranscriptionQueue(new VideoToNo(http), TimeSpan.Zero).Run(batch, files, _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()),
        () => submissions > 0, _ => { }, token);
    Check(first.Status == WorkStatus.Succeeded && second.Status == WorkStatus.Ready && submissions == 1, "只处理了当前条");
});
await TestSync("YouTube 链接归一化和频道标签", () =>
{
    var aliases = new[]
    {
        "https://www.youtube.com/watch?list=ignored&v=jNQXAC9IVRw&t=3",
        "https://youtu.be/jNQXAC9IVRw?si=ignored",
        "https://www.youtube.com/shorts/jNQXAC9IVRw",
        "https://m.youtube.com/live/jNQXAC9IVRw"
    };
    var urls = aliases.Select(alias => Works.FromUrl(Platform.YouTube, new Uri(alias), null, "").Url).Distinct().ToArray();
    Check(urls is ["https://www.youtube.com/watch?v=jNQXAC9IVRw"], "同一作品使用同一标准链接");
    var channel = Works.Profile(Platform.YouTube, new Uri("https://m.youtube.com/@YouTube/shorts?si=ignored"));
    Check(channel.Id == "@YouTube" && channel.Page.AbsoluteUri == "https://www.youtube.com/@YouTube/shorts", "保留 Shorts 标签");
    Check(!Works.IsPlatform(Platform.YouTube, "youtube.com.example.org"), "只接受平台域名");
});
await TestSync("YouTube 首屏、续页和实际发布日期", () =>
{
    using var initial = JsonDocument.Parse("""
        {
          "metadata":{"channelMetadataRenderer":{"externalId":"channel"}},
          "contents":{"twoColumnBrowseResultsRenderer":{"tabs":[
            {"tabRenderer":{"endpoint":{"commandMetadata":{"webCommandMetadata":{"url":"/@test/videos"}}},
             "selected":true,"content":{"richGridRenderer":{"contents":[
              {"richItemRenderer":{"content":{"compactVideoRenderer":{"videoId":"jNQXAC9IVRw","title":{"runs":[{"text":"样本"}]}}}}},
              {"continuationItemRenderer":{"continuationEndpoint":{"continuationCommand":{"token":"next"}}}}
             ]}}}},
            {"tabRenderer":{"endpoint":{"commandMetadata":{"webCommandMetadata":{"url":"/@test/shorts"}}}}}
          ]}}
        }
        """);
    var tab = YouTubeListing.Initial(initial.RootElement);
    Check(tab?.Page is { Next: "next" } page && page.Videos.Single().Title == "样本" && tab.Available.Contains("shorts"), "首屏视频和续页");
    using var continuation = JsonDocument.Parse("""
        {"onResponseReceivedActions":[{"appendContinuationItemsAction":{"continuationItems":[
           {"richItemRenderer":{"content":{"shortsLockupViewModel":{"entityId":"shorts-shelf-item-n0FM32lZA8s",
                "overlayMetadata":{"primaryText":{"content":"Short"}}}}}}
        ]}}]}
        """);
    Check(YouTubeListing.Continuation(continuation.RootElement)?.Videos.Single().Id == "n0FM32lZA8s", "Shorts 续页");
    Check(YouTubeListing.PublishedDate("""<meta itemprop="datePublished" content="2026-06-12T10:00:00-07:00">""") == from,
        "YouTube 发布时间按北京时间换日");
    Check(YouTubeListing.PublishedDate("""<meta itemprop="datePublished" content="2026-06-13">""") is null, "仅日期不能确认时区");
});
await Test("YouTube 提交、输出和保存后重开", async () =>
{
    var work = Works.FromUrl(Platform.YouTube, new Uri("https://youtu.be/jNQXAC9IVRw"), from, "样本");
    var batch = new Batch { Platform = Platform.YouTube, Items = [work] };
    var files = new BatchFiles(Path.Combine(scratch, "youtube"));
    var submitted = "";
    using var http = Fake(req =>
    {
        if (req.Method == HttpMethod.Post) submitted = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return Service(req);
    });
    await new TranscriptionQueue(new VideoToNo(http), TimeSpan.Zero).Run(batch, files,
        _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["unused"] = "fake" }),
        () => false, _ => { }, token);
    Check(submitted.Contains(work.Url) && !submitted.Contains("cookie"), "YouTube 提交使用标准 URL");
    Check(work.Status == WorkStatus.Succeeded && work.MarkdownPath!.EndsWith("2026-06-13_youtube_jNQXAC9IVRw.md"), "YouTube 文件名");
    Check(files.Load().Items.Single().Platform == Platform.YouTube, "可重新打开 YouTube 批次");
});
Console.WriteLine("12 项验收测试通过。");

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static async Task Test(string name, Func<Task> action)
{
    await action();
    Console.WriteLine($"PASS {name}");
}
static Task TestSync(string name, Action action)
{
    action();
    Console.WriteLine($"PASS {name}");
    return Task.CompletedTask;
}
static long Stamp(string date) => DateTimeOffset.Parse(date).ToUnixTimeSeconds();
static Work Video(string number) => Works.FromUrl(Platform.Douyin, new Uri($"https://www.douyin.com/video/{number}"), null, "");
static string Bili(int pn, int ps, int count, params (string id, long time, bool pinned)[] items) =>
    JsonSerializer.Serialize(new { code = 0, data = new { page = new { pn, ps, count },
        list = new { vlist = items.Select(v => new { bvid = v.id, title = "样本", created = v.time, is_top = v.pinned }) } } });
static string Douyin(long max_cursor, bool has_more, params (string id, long time, bool pinned)[] items) =>
    JsonSerializer.Serialize(new { status_code = 0, max_cursor, has_more, aweme_list = items.Select(v =>
        new { aweme_id = v.id, create_time = v.time, is_top = v.pinned, desc = "样本", video = new { duration = 1000 } }) });
(Batch batch, BatchFiles files) Save(string name, params Work[] items)
{
    var batch = new Batch { Platform = Platform.Douyin, Items = [.. items] };
    var files = new BatchFiles(Path.Combine(scratch, name));
    files.Save(batch);
    return (batch, files);
}
static HttpClient Fake(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new Handler(respond)) { BaseAddress = new Uri("http://127.0.0.1:8000/") };
static HttpResponseMessage Json(string text) => new() { Content = new StringContent(text, Encoding.UTF8, "application/json") };
HttpResponseMessage Service(HttpRequestMessage request)
{
    if (request.Method == HttpMethod.Post) return Json(JsonSerializer.Serialize(new { task_id = taskId }));
    if (request.RequestUri!.AbsolutePath == "/api/health") return Json("{\"service\":\"VideoToNo\",\"version\":\"1.4.7\",\"status\":\"ok\"}");
    if (request.RequestUri.Query.Contains("json")) return Json("{\"segments\":[{\"start\":0,\"end\":3,\"text\":\"样本\"}]}");
    if (request.RequestUri.Query.Contains("markdown")) return Json(JsonSerializer.Serialize(new { title = "样本标题", text = "[00:00-00:03] 样本" }));
    return Json("{\"status\":\"completed\"}");
}
Task Run(HttpClient http, Batch batch, BatchFiles files) =>
    new TranscriptionQueue(new VideoToNo(http), TimeSpan.Zero).Run(batch, files,
        _ => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>()), () => false, _ => { }, token);

sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(respond(request));
}
