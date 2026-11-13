using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.VisualBasic.FileIO;

namespace VideoMarkdown;

public enum Platform { Bilibili, Douyin, YouTube }

public enum WorkStatus
{
    Ready, Submitting, Running, Succeeded, NoTranscript, Failed,
    ExportFailed, SubmissionUnknown
}

public sealed class Work
{
    public required Platform Platform { get; init; }
    public required string Id { get; init; }
    public required string Url { get; init; }
    public string Title { get; set; } = "";
    public DateOnly? Published { get; set; }
    public bool Selected { get; set; }
    public WorkStatus Status { get; set; }
    public string? TaskId { get; set; }
    public string? MarkdownPath { get; set; }
    public string? Error { get; set; }

    public string PlatformName => Platform switch
    {
        Platform.Bilibili => "B 站",
        Platform.Douyin => "抖音",
        _ => "YouTube"
    };
    public string DisplayDate => Published?.ToString("yyyy-MM-dd") ?? "日期待确认";
    public string DisplayTitle => Title.Length == 0 ? Id : Title;
    public string DisplayStatus => Status switch
    {
        WorkStatus.Ready => "待处理",
        WorkStatus.Submitting => "提交中",
        WorkStatus.Running => "转录中",
        WorkStatus.Succeeded => "已导出",
        WorkStatus.NoTranscript => "无有效转录",
        WorkStatus.Failed => "转录失败",
        WorkStatus.ExportFailed => "导出失败",
        WorkStatus.SubmissionUnknown => "提交结果未知",
        _ => Status.ToString()
    };
}

public sealed record ImportRow(int Line, string Url, DateOnly? Published, string Title);

public static partial class Works
{
    public static DateOnly BeijingToday => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8)).DateTime);
    public static DateOnly BeijingDate(long seconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(TimeSpan.FromHours(8)).DateTime);

    [GeneratedRegex(@"^/video/(BV[A-Za-z0-9]{10}|av[0-9]+)(?:/|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BiliVideo();
    [GeneratedRegex(@"^/(?:share/)?video/([0-9]{8,25})(?:/|$)")]
    private static partial Regex DouyinVideo();
    [GeneratedRegex(@"^/([0-9]+)(?:/video)?/?$")]
    private static partial Regex BiliProfile();
    [GeneratedRegex(@"^/(?:share/)?user/([A-Za-z0-9_-]+)/*$")]
    private static partial Regex DouyinProfile();
    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    internal static partial Regex YouTubeId();
    [GeneratedRegex(@"^/(@[^/]+|channel/UC[A-Za-z0-9_-]{22}|(?:c|user)/[^/]+)(?:/(featured|videos|shorts|streams|playlists|community|posts|about))?/?$")]
    private static partial Regex YouTubeProfile();
    [GeneratedRegex(@"https?://[^\s<>""'，。！？、；【】（）]+", RegexOptions.IgnoreCase)]
    public static partial Regex Links();

    public static bool IsBili(string host) => host == "bilibili.com" || host.EndsWith(".bilibili.com", StringComparison.Ordinal);
    public static bool IsDouyin(string host) => host is "douyin.com" or "iesdouyin.com"
        || host.EndsWith(".douyin.com", StringComparison.Ordinal) || host.EndsWith(".iesdouyin.com", StringComparison.Ordinal);
    public static bool IsYouTube(string host) => host is "youtube.com" or "www.youtube.com" or "m.youtube.com";
    public static bool IsShort(Platform platform, string host) => platform switch
    {
        Platform.Bilibili => host == "b23.tv",
        Platform.Douyin => host == "v.douyin.com",
        _ => false // youtu.be already contains the video ID
    };
    public static bool IsPlatform(Platform platform, string host) =>
        platform switch
        {
            Platform.Bilibili => IsBili(host) || IsShort(platform, host),
            Platform.Douyin => IsDouyin(host) || IsShort(platform, host),
            Platform.YouTube => IsYouTube(host) || host == "youtu.be",
            _ => false
        };

    public static Uri ParseUrl(string value)
    {
        var link = Links().Match(value);
        if (!link.Success || !Uri.TryCreate(link.Value.TrimEnd(')', ']', '.', ',', ';'), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("https" or "http") || !uri.IsDefaultPort || uri.UserInfo.Length != 0)
            throw new FormatException("请输入有效的平台链接。");
        return uri;
    }

    public static (Uri Page, string Id) Profile(Platform platform, Uri url)
    {
        if (platform == Platform.YouTube && IsYouTube(url.Host))
        {
            var channel = YouTubeProfile().Match(url.AbsolutePath);
            if (channel.Success)
            {
                var key = channel.Groups[1].Value;
                var tab = channel.Groups[2].Value == "shorts" ? "shorts" : "videos";
                return (new Uri($"https://www.youtube.com/{key}/{tab}"), key);
            }
        }
        var match = platform == Platform.Bilibili && url.Host == "space.bilibili.com"
            ? BiliProfile().Match(url.AbsolutePath)
            : platform == Platform.Douyin && IsDouyin(url.Host)
                ? DouyinProfile().Match(url.AbsolutePath)
                : Match.Empty;
        if (!match.Success || match.Groups[1].Value == "self")
            throw new FormatException("需要作者主页链接。作品链接可在导入区使用。");
        var id = match.Groups[1].Value;
        var page = platform == Platform.Bilibili
            ? $"https://space.bilibili.com/{id}/video"
            : $"https://www.douyin.com/user/{id}";
        return (new Uri(page), id);
    }

    public static Work FromUrl(Platform platform, Uri url, DateOnly? date, string title)
    {
        if (!IsPlatform(platform, url.Host))
            throw new FormatException("链接与所选平台不一致。");
        if (platform == Platform.YouTube)
        {
            var parts = url.AbsolutePath.Trim('/').Split('/');
            var videoId = url.Host == "youtu.be" && parts.Length == 1 ? parts[0]
                : url.Host != "youtu.be" && parts is ["watch"] ? Query(url, "v")
                : url.Host != "youtu.be" && parts.Length == 2 && parts[0] is "shorts" or "live" or "embed" ? parts[1]
                : "";
            if (!YouTubeId().IsMatch(videoId)) throw new FormatException("链接没有指向单条 YouTube 视频。");
            return new Work { Platform = platform, Id = videoId, Url = $"https://www.youtube.com/watch?v={videoId}",
                Published = date, Title = title.Trim(), Selected = date.HasValue };
        }
        var match = (platform == Platform.Bilibili ? BiliVideo() : DouyinVideo()).Match(url.AbsolutePath);
        if (!match.Success)
            throw new FormatException("链接没有指向单条视频。");
        var id = match.Groups[1].Value;
        if (platform == Platform.Bilibili)
            id = (id.StartsWith("av", StringComparison.OrdinalIgnoreCase) ? "av" : "BV") + id[2..];
        var baseUrl = platform == Platform.Bilibili ? "https://www.bilibili.com/video/" : "https://www.douyin.com/video/";
        return new Work { Platform = platform, Id = id, Url = baseUrl + id, Published = date, Title = title.Trim(), Selected = date.HasValue };
    }

    public static async Task<Uri> ResolveShortAsync(HttpClient client, Platform platform, Uri uri, CancellationToken token)
    {
        if (!IsShort(platform, uri.Host))
            return uri;
        for (var i = 0; i < 6; i++)
        {
            if (!IsPlatform(platform, uri.Host) || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Scheme is not ("http" or "https"))
                throw new FormatException("短链跳转到了平台以外的地址。");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (IsPlatform(platform, uri.Host) && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Scheme is "http" or "https"
                    && !IsShort(platform, uri.Host))
                    return uri;
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"短链返回 HTTP {(int)response.StatusCode}。");
            throw new FormatException("短链没有返回作品或主页地址。");
        }
        throw new FormatException("短链跳转次数过多。");
    }

    public static (List<ImportRow> Rows, List<string> Errors) ReadCsv(TextReader source)
    {
        using var parser = new TextFieldParser(source);
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        var headers = parser.ReadFields()?.Select(field => field.Trim().TrimStart('\uFEFF')).ToList() ?? [];
        var urlIndex = headers.FindIndex(field => field.Equals("url", StringComparison.OrdinalIgnoreCase));
        var dateIndex = headers.FindIndex(field => field.Equals("published_date", StringComparison.OrdinalIgnoreCase));
        var titleIndex = headers.FindIndex(field => field.Equals("title", StringComparison.OrdinalIgnoreCase));
        if (urlIndex < 0)
            throw new FormatException("CSV 表头必须包含 url。");

        var rows = new List<ImportRow>();
        var errors = new List<string>();
        while (!parser.EndOfData)
        {
            var line = (int)parser.LineNumber;
            try
            {
                var fields = parser.ReadFields() ?? [];
                var url = ReadField(fields, urlIndex);
                var date = ReadField(fields, dateIndex);
                if (url.Length == 0)
                    throw new FormatException("url 为空。");
                DateOnly? published = null;
                if (date.Length != 0)
                {
                    if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
                        throw new FormatException("发布日期须为 YYYY-MM-DD。");
                    published = parsed;
                }
                rows.Add(new ImportRow(line, url, published, ReadField(fields, titleIndex)));
            }
            catch (MalformedLineException) { errors.Add($"第 {line} 行：CSV 引号格式有误。"); }
            catch (FormatException error) { errors.Add($"第 {line} 行：{error.Message}"); }
        }
        return (rows, errors);
    }

    private static string ReadField(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index].Trim() : "";

    public static string Query(Uri uri, string key)
    {
        foreach (var pair in uri.Query.TrimStart('?').Split('&'))
        {
            var fields = pair.Split('=', 2);
            if (fields.Length == 2 && fields[0] == key) return Uri.UnescapeDataString(fields[1]);
        }
        return "";
    }
}
