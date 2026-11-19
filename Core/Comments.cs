using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoMarkdown;

public enum CommentState { None, Saved, Partial, Failed }

public sealed record VideoComment(string Id, string Author, string Text, string Published, string Likes, string ParentId = "");

public sealed record CommentSnapshot(IReadOnlyList<VideoComment> Items, CommentState State, string Notice)
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class CommentReader
{
    public const int Limit = 200;
    private readonly Dictionary<string, VideoComment> items = new(StringComparer.Ordinal);
    public int Count => items.Count;
    public bool Received { get; private set; }
    public bool EndReached { get; private set; }
    public bool HasReplies { get; private set; }
    public bool AtLimit => Count >= Limit;

    public void Accept(Platform platform, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        switch (platform)
        {
            case Platform.Bilibili:
                if (root.Get("code").Number() != 0) return;
                var data = root.Get("data");
                var replies = data.Get("replies");
                var cursor = data.Get("cursor");
                if (replies.ValueKind == JsonValueKind.Array)
                {
                    Received = true;
                    ReadBili(replies, "");
                }
                var top = data.Get("top_replies");
                if (top.ValueKind == JsonValueKind.Array) ReadBili(top, "");
                if (cursor.Get("is_end").Flag())
                {
                    Received = true;
                    EndReached = true;
                }
                break;
            case Platform.Douyin:
                if (root.Get("status_code").Number() != 0) return;
                var comments = root.Get("comments");
                if (comments.ValueKind != JsonValueKind.Array) return;
                Received = true;
                ReadDouyin(comments, "");
                EndReached = root.Get("has_more").ValueKind != JsonValueKind.Undefined && !root.Get("has_more").Flag();
                break;
            case Platform.YouTube:
                ReadYouTube(root);
                break;
        }
    }

    public CommentSnapshot Snapshot(string issue)
    {
        var notice = AtLimit ? $"达到每条视频 {Limit} 条评论的保存上限。" : issue;
        if (!Received)
            return new CommentSnapshot([], CommentState.Failed,
                notice.Length > 0 ? notice : "未读取到评论数据，可在平台页面登录或验证后重试。");
        if (Count == 0 && !EndReached)
            return new CommentSnapshot([], CommentState.Failed, "页面未返回可确认的评论列表，请登录或验证后重试。");
        if (Count == 0)
            return new CommentSnapshot([], CommentState.Saved, "本次平台返回的评论列表为空。");
        if (notice.Length == 0)
            notice = EndReached && !HasReplies ? "本次已加载的评论列表读取结束。" : "已保存本次页面加载的评论，更多评论或未展开回复可能尚未收齐。";
        var state = AtLimit || !EndReached || HasReplies || issue.Length > 0 ? CommentState.Partial : CommentState.Saved;
        return new CommentSnapshot(items.Values.ToList(), state, notice);
    }

    private void ReadBili(JsonElement replies, string parent)
    {
        foreach (var reply in replies.EnumerateArray())
        {
            var id = reply.Get("rpid_str").StringValue();
            if (id.Length == 0) id = reply.Get("rpid").StringValue();
            var root = reply.Get("root_str").StringValue();
            if (root.Length == 0) root = reply.Get("root").StringValue();
            if (root == "0") root = "";
            Add(new VideoComment(id, reply.Get("member").Get("uname").StringValue(),
                reply.Get("content").Get("message").StringValue(), Date(reply.Get("ctime")),
                reply.Get("like").StringValue(), parent.Length > 0 ? parent : root));
            HasReplies |= reply.Get("rcount").Number() > 0;
            var nested = reply.Get("replies");
            if (nested.ValueKind == JsonValueKind.Array) ReadBili(nested, id);
        }
    }

    private void ReadDouyin(JsonElement comments, string parent)
    {
        foreach (var comment in comments.EnumerateArray())
        {
            var id = comment.Get("cid").StringValue();
            var reply = comment.Get("reply_id").StringValue();
            if (reply == "0") reply = "";
            Add(new VideoComment(id, comment.Get("user").Get("nickname").StringValue(), comment.Get("text").StringValue(),
                Date(comment.Get("create_time")), comment.Get("digg_count").StringValue(), parent.Length > 0 ? parent : reply));
            HasReplies |= comment.Get("reply_comment_total").Number() > 0;
            var nested = comment.Get("reply_comment");
            if (nested.ValueKind == JsonValueKind.Array) ReadDouyin(nested, id);
        }
    }

    private void ReadYouTube(JsonElement root)
    {
        Walk(root, value =>
        {
            var entity = value.Get("commentEntityPayload");
            if (entity.ValueKind == JsonValueKind.Object)
            {
                var properties = entity.Get("properties");
                var id = properties.Get("commentId").StringValue();
                Add(new VideoComment(id, entity.Get("author").Get("displayName").StringValue(),
                    properties.Get("content").Get("content").StringValue(), properties.Get("publishedTime").StringValue(),
                    entity.Get("toolbar").Get("likeCountNotliked").StringValue(), YouTubeParent(id)));
            }
            var legacy = value.Get("commentRenderer");
            if (legacy.ValueKind == JsonValueKind.Object)
            {
                var id = legacy.Get("commentId").StringValue();
                Add(new VideoComment(id, Text(legacy.Get("authorText")), Text(legacy.Get("contentText")),
                    Text(legacy.Get("publishedTimeText")), Text(legacy.Get("voteCount")), YouTubeParent(id)));
            }
            if (value.Get("commentRepliesRenderer").ValueKind == JsonValueKind.Object) HasReplies = true;
        });
        if (Count > 0) Received = true;
    }

    private void Add(VideoComment comment)
    {
        if (comment.Id.Length == 0 || string.IsNullOrWhiteSpace(comment.Text) || AtLimit) return;
        items.TryAdd(comment.Id, comment);
    }

    private static string YouTubeParent(string id) => id.IndexOf('.') is > 0 and var dot ? id[..dot] : "";

    private static string Date(JsonElement value)
    {
        if (value.Number() is not { } timestamp) return "";
        try { return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToOffset(TimeSpan.FromHours(8)).ToString("yyyy-MM-dd HH:mm:ss 'UTC+8'"); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    private static string Text(JsonElement value) => value.Get("simpleText").StringValue() is { Length: > 0 } text
        ? text : value.Get("runs").ValueKind == JsonValueKind.Array
            ? string.Concat(value.Get("runs").EnumerateArray().Select(run => run.Get("text").StringValue())) : "";

    private static void Walk(JsonElement value, Action<JsonElement> visit)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            visit(value);
            foreach (var field in value.EnumerateObject()) Walk(field.Value, visit);
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) Walk(item, visit);
    }
}

public static class CommentMarkdown
{
    public const string Start = "<!-- video-markdown-comments:start -->";
    public const string End = "<!-- video-markdown-comments:end -->";

    public static string Render(CommentSnapshot snapshot)
    {
        var output = new StringBuilder();
        output.AppendLine(Start).AppendLine("## 视频评论").AppendLine();
        output.AppendLine($"采集时间 {snapshot.CapturedAt.ToOffset(TimeSpan.FromHours(8)):yyyy-MM-dd HH:mm:ss} UTC+8 · 已保存 {snapshot.Items.Count} 条");
        output.AppendLine().AppendLine(Escape(snapshot.Notice)).AppendLine();
        foreach (var comment in snapshot.Items)
        {
            var meta = new[] { comment.Author, comment.Published, comment.Likes.Length > 0 ? $"点赞 {comment.Likes}" : "" }
                .Where(value => value.Length > 0);
            output.AppendLine($"### {Escape(string.Join(" · ", meta))}");
            output.AppendLine($"评论 ID {Escape(comment.Id)}{(comment.ParentId.Length > 0 ? $" · 回复 {Escape(comment.ParentId)}" : "")}").AppendLine();
            foreach (var line in comment.Text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
                output.Append("> ").AppendLine(Escape(line));
            output.AppendLine();
        }
        return output.AppendLine(End).ToString();
    }

    private static string Escape(string value)
    {
        var encoded = WebUtility.HtmlEncode(value.Replace('\r', ' ').Replace('\n', ' '));
        return Regex.Replace(encoded, @"([\\\x60*_\[\]#|])", @"\$1");
    }
}
