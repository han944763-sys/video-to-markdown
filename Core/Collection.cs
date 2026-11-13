using System.Text.Json;

namespace VideoMarkdown;

public sealed class Collection(Platform platform, DateOnly from, DateOnly through)
{
    private readonly HashSet<int> biliPages = [];
    private readonly HashSet<string> douyinCursors = [];
    private readonly Dictionary<string, Work> works = new(StringComparer.Ordinal);
    private string nextCursor = "0";
    private bool hasIncompleteData;
    public DateOnly? Oldest { get; private set; }
    public DateOnly? Newest { get; private set; }
    public bool Complete { get; private set; }
    public bool Exhausted { get; private set; }
    public int Pages => biliPages.Count + douyinCursors.Count;
    public IReadOnlyCollection<Work> WorksFound => works.Values;

    public bool Accept(string json, string cursor)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (platform == Platform.Bilibili)
            return AcceptBili(root, cursor);
        return AcceptDouyin(root, cursor);
    }

    private bool AcceptBili(JsonElement root, string cursor)
    {
        if (root.Get("code").Number() != 0 || !int.TryParse(cursor, out var page)
            || page < 1 || (page > 1 && !biliPages.Contains(page - 1)))
            return false;
        var data = root.Get("data");
        var items = data.Get("list").Get("vlist");
        var info = data.Get("page");
        var count = info.Get("count").Number();
        var size = info.Get("ps").Number();
        if (items.ValueKind != JsonValueKind.Array || count is null or < 0 || size is null or <= 0
            || info.Get("pn").Number() != page || (items.GetArrayLength() == 0 && count > (page - 1L) * size)
            || !biliPages.Add(page))
            return false;
        var dates = new List<DateOnly>();
        var uncertain = false;
        foreach (var item in items.EnumerateArray())
        {
            var date = Add(item.Get("bvid").StringValue(), item.Get("title").StringValue(), item.Get("created").Number());
            var pinned = item.Get("is_top").Flag() || item.Get("is_pinned").Flag();
            if (date.HasValue && !pinned) dates.Add(date.Value);
            if (!date.HasValue) uncertain = true;
        }
        hasIncompleteData |= uncertain;
        Exhausted = page * size >= count;
        Complete = !hasIncompleteData && (Exhausted || (page > 1 && Older(dates)));
        return true;
    }

    private bool AcceptDouyin(JsonElement root, string cursor)
    {
        var items = root.Get("aweme_list");
        var more = root.Get("has_more");
        if (root.Get("status_code").Number() != 0 || items.ValueKind != JsonValueKind.Array
            || more.ValueKind is not (JsonValueKind.True or JsonValueKind.False or JsonValueKind.Number)
            || cursor != nextCursor || douyinCursors.Contains(cursor))
            return false;
        var following = root.Get("max_cursor").StringValue();
        douyinCursors.Add(cursor);
        nextCursor = following;
        var dates = new List<DateOnly>();
        var uncertain = false;
        foreach (var item in items.EnumerateArray())
        {
            var id = item.Get("aweme_id").StringValue();
            var stamp = item.Get("create_time").Number();
            var hasImages = item.Get("images").ValueKind == JsonValueKind.Array && item.Get("images").GetArrayLength() > 0;
            var isVideo = !hasImages && item.Get("video").ValueKind == JsonValueKind.Object;
            var date = isVideo ? Add(id, item.Get("desc").StringValue(), stamp) : SafeDate(stamp);
            var pinned = item.Get("is_top").Flag() || item.Get("is_pinned").Flag();
            // The first page can contain older pinned posts mixed with recent ones.
            if (date.HasValue && !pinned && cursor != "0") dates.Add(date.Value);
            if (!date.HasValue) uncertain = true;
        }
        var stalled = more.Flag() && (following.Length == 0 || douyinCursors.Contains(following) || items.GetArrayLength() == 0);
        hasIncompleteData |= uncertain || stalled;
        Exhausted = !more.Flag() || stalled;
        Complete = !hasIncompleteData && (Exhausted || Older(dates));
        return true;
    }

    private bool Older(List<DateOnly> dates) => dates.Count > 0 && dates.All(date => date < from);

    private DateOnly? Add(string id, string title, long? seconds)
    {
        var date = SafeDate(seconds);
        if (date is null)
            return null;
        try
        {
            var host = platform == Platform.Bilibili ? "www.bilibili.com" : "www.douyin.com";
            var work = VideoMarkdown.Works.FromUrl(platform, new Uri($"https://{host}/video/{id}"), date, title);
            if (date >= from && date <= through) works.TryAdd(work.Id, work);
            if (Oldest is null || date < Oldest) Oldest = date;
            if (Newest is null || date > Newest) Newest = date;
            return date;
        }
        catch (FormatException) { return null; }
    }

    private static DateOnly? SafeDate(long? seconds)
    {
        if (seconds is null or <= 0) return null;
        try { return VideoMarkdown.Works.BeijingDate(seconds.Value); }
        catch (ArgumentOutOfRangeException) { return null; }
    }
}

public static class JsonFields
{
    public static JsonElement Get(this JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var field) ? field : default;

    public static string StringValue(this JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number => value.GetRawText(),
        _ => ""
    };

    public static long? Number(this JsonElement value) => long.TryParse(value.StringValue(), out var number) ? number : null;
    public static bool Flag(this JsonElement value) => value.ValueKind == JsonValueKind.True || value.Number() is > 0;
}
