using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoMarkdown;

public sealed record YouTubeEntry(string Id, string Title);
public sealed record YouTubePage(IReadOnlyList<YouTubeEntry> Videos, string Next, bool UnrecognizedItems);
public sealed record YouTubeTab(string Name, IReadOnlySet<string> Available, YouTubePage? Page);

public static partial class YouTubeListing
{
    public static YouTubeTab? Initial(JsonElement data)
    {
        if (data.Get("metadata").Get("channelMetadataRenderer").Get("externalId").StringValue().Length == 0)
            return null;
        var contents = data.Get("contents");
        var tabs = contents.Get("twoColumnBrowseResultsRenderer").Get("tabs");
        if (tabs.ValueKind != JsonValueKind.Array) tabs = contents.Get("singleColumnBrowseResultsRenderer").Get("tabs");
        if (tabs.ValueKind != JsonValueKind.Array) return null;
        var available = new HashSet<string>(StringComparer.Ordinal);
        JsonElement current = default;
        string name = "";
        foreach (var node in tabs.EnumerateArray())
        {
            var tab = node.Get("tabRenderer");
            var url = tab.Get("endpoint").Get("commandMetadata").Get("webCommandMetadata").Get("url").StringValue();
            var tabName = url.Split('?', 2)[0].TrimEnd('/').Split('/').Last();
            if (tabName is "videos" or "shorts") available.Add(tabName);
            if (!tab.Get("selected").Flag()) continue;
            name = tabName;
            current = tab.Get("content");
        }
        var items = current.Get("richGridRenderer").Get("contents");
        return new YouTubeTab(name, available, items.ValueKind == JsonValueKind.Array ? ParsePage(items) : null);
    }

    public static YouTubePage? Continuation(JsonElement data)
    {
        foreach (var field in new[] { "onResponseReceivedActions", "onResponseReceivedEndpoints" })
        {
            var actions = data.Get(field);
            if (actions.ValueKind != JsonValueKind.Array) continue;
            foreach (var action in actions.EnumerateArray())
            {
                var command = action.Get("appendContinuationItemsAction");
                var items = command.Get("continuationItems");
                if (items.ValueKind == JsonValueKind.Array) return ParsePage(items);
            }
        }
        return null;
    }

    private static YouTubePage ParsePage(JsonElement items)
    {
        var entries = new List<YouTubeEntry>();
        var next = "";
        var unknown = false;
        foreach (var item in items.EnumerateArray())
        {
            var continuation = item.Get("continuationItemRenderer");
            if (continuation.ValueKind == JsonValueKind.Object)
            {
                var token = continuation.Get("continuationEndpoint").Get("continuationCommand").Get("token").StringValue();
                if (next.Length != 0 || token.Length == 0) unknown = true;
                else next = token;
                continue;
            }
            var content = item.Get("richItemRenderer").Get("content");
            if (content.ValueKind != JsonValueKind.Object) continue;
            var entry = ParseEntry(content);
            if (entry is null) unknown = true;
            else entries.Add(entry);
        }
        return new YouTubePage(entries, next, unknown);
    }

    private static YouTubeEntry? ParseEntry(JsonElement content)
    {
        var video = content.Get("videoRenderer");
        if (video.ValueKind != JsonValueKind.Object) video = content.Get("compactVideoRenderer");
        var id = video.Get("videoId").StringValue();
        var title = ReadText(video.Get("title"));
        if (id.Length == 0)
        {
            var shortVideo = content.Get("shortsLockupViewModel");
            id = shortVideo.Get("onTap").Get("innertubeCommand").Get("reelWatchEndpoint").Get("videoId").StringValue();
            var entity = shortVideo.Get("entityId").StringValue();
            if (id.Length == 0 && entity.StartsWith("shorts-shelf-item-", StringComparison.Ordinal))
                id = entity["shorts-shelf-item-".Length..];
            title = ReadText(shortVideo.Get("overlayMetadata").Get("primaryText"));
        }
        if (id.Length == 0)
        {
            var lockup = content.Get("lockupViewModel");
            if (lockup.Get("contentType").StringValue() == "LOCKUP_CONTENT_TYPE_VIDEO")
            {
                id = lockup.Get("contentId").StringValue();
                title = ReadText(lockup.Get("metadata").Get("lockupMetadataViewModel").Get("title"));
            }
        }
        return Works.YouTubeId().IsMatch(id) ? new YouTubeEntry(id, title) : null;
    }

    private static string ReadText(JsonElement value)
    {
        var text = value.Get("content").StringValue();
        if (text.Length == 0) text = value.Get("simpleText").StringValue();
        var runs = value.Get("runs");
        return text.Length > 0 || runs.ValueKind != JsonValueKind.Array ? text :
            string.Concat(runs.EnumerateArray().Select(run => run.Get("text").StringValue()));
    }

    [GeneratedRegex("""<meta\s+itemprop=["'](?:datePublished|uploadDate)["']\s+content=["']([^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex DateMeta();
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase)]
    private static partial Regex Timestamp();

    public static DateOnly? PublishedDate(string html)
    {
        foreach (Match match in DateMeta().Matches(html))
        {
            var value = WebUtility.HtmlDecode(match.Groups[1].Value);
            if (Timestamp().IsMatch(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
                return Works.BeijingDate(time.ToUnixTimeSeconds());
        }
        return null;
    }
}
