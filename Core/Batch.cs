using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoMarkdown;

public sealed class Batch
{
    public int Version { get; init; } = 1;
    public Platform Platform { get; set; }
    public string Profile { get; set; } = "";
    public DateOnly From { get; set; } = Works.BeijingToday.AddMonths(-5);
    public DateOnly Through { get; set; } = Works.BeijingToday;
    public string CollectionStatus { get; set; } = "";
    public bool IncludeComments { get; set; }
    public List<Work> Items { get; init; } = [];
}

public sealed class BatchFiles(string directory)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        IgnoreReadOnlyProperties = true
    };
    public const string StateName = "批次状态.json";
    public string DirectoryPath { get; } = Path.GetFullPath(directory);

    public Batch Load()
    {
        var batch = JsonSerializer.Deserialize<Batch>(File.ReadAllText(Path.Combine(DirectoryPath, StateName)), JsonOptions)
            ?? throw new InvalidDataException("批次文件为空。");
        if (batch.Version != 1 || batch.From > batch.Through || !Enum.IsDefined(batch.Platform)
            || batch.Items.Select(work => work.Id).Distinct(StringComparer.Ordinal).Count() != batch.Items.Count
            || batch.Items.Any(work => work.Platform != batch.Platform || !IsValidWork(work)))
            throw new InvalidDataException("批次文件包含不支持的数据。");
        foreach (var work in batch.Items.Where(work => work.Status == WorkStatus.Submitting))
        {
            work.Status = WorkStatus.SubmissionUnknown;
            work.Error = "程序在收到任务 ID 前关闭。确认 VideoToNo 未处理该条后，可手动重试。";
        }
        return batch;
    }

    public void SaveState(Batch batch)
    {
        Directory.CreateDirectory(DirectoryPath);
        AtomicWrite(Path.Combine(DirectoryPath, StateName), JsonSerializer.Serialize(batch, JsonOptions));
    }

    public void Save(Batch batch)
    {
        SaveState(batch);
        SaveCsv(batch);
    }

    private void SaveCsv(Batch batch)
    {
        var csv = new StringBuilder("platform,video_id,published_date,title,url,selected,status,task_id,md_path,error,comment_status,comment_count,comment_notice\r\n");
        foreach (var work in batch.Items)
        {
            string?[] fields = [work.PlatformName, work.Id, work.Published?.ToString("yyyy-MM-dd"), work.Title, work.Url,
                work.Selected ? "1" : "0", work.DisplayStatus, work.TaskId, work.MarkdownPath, work.Error,
                work.CommentStatus.ToString(), work.CommentCount.ToString(), work.CommentNotice];
            csv.AppendLine(string.Join(',', fields.Select(Quote)));
        }
        AtomicWrite(Path.Combine(DirectoryPath, "作品清单.csv"), csv.ToString(), new UTF8Encoding(true));
    }

    public string Export(Work work, string title, string markdown)
    {
        var date = work.Published?.ToString("yyyy-MM-dd") ?? "日期未知";
        var platform = work.Platform.ToString().ToLowerInvariant();
        var relative = Path.Combine("转录稿", $"{date}_{platform}_{work.Id}.md");
        var target = Path.Combine(DirectoryPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var heading = SingleLine(work.Title.Length == 0 ? title : work.Title);
        if (heading.Length == 0) heading = work.Id;
        var body = $"# {heading}\n\n- 发布日期：{date}\n- 原视频：<{work.Url}>\n\n{markdown.Trim()}\n";
        if (File.Exists(target))
        {
            if (File.ReadAllText(target) != body)
                throw new IOException("同名 Markdown 已存在且内容不同。移走该文件后可重试导出。");
            return relative;
        }
        AtomicWrite(target, body, replace: false);
        return relative;
    }

    public void WriteComments(Work work, CommentSnapshot snapshot)
    {
        if (work.MarkdownPath is not { } relative || Path.IsPathFullyQualified(relative))
            throw new IOException("找不到对应视频的 Markdown 路径。");
        var target = Path.GetFullPath(Path.Combine(DirectoryPath, relative));
        var folder = Path.Combine(DirectoryPath, "转录稿") + Path.DirectorySeparatorChar;
        if (!target.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
            throw new IOException("视频 Markdown 路径超出批次转录目录。");
        var original = File.ReadAllText(target);
        var start = original.IndexOf(CommentMarkdown.Start, StringComparison.Ordinal);
        var end = original.IndexOf(CommentMarkdown.End, StringComparison.Ordinal);
        var section = CommentMarkdown.Render(snapshot);
        string updated;
        if (start < 0 && end < 0) updated = original.TrimEnd() + "\n\n" + section;
        else
        {
            if (start < 0 || end < start
                || original.IndexOf(CommentMarkdown.Start, start + CommentMarkdown.Start.Length, StringComparison.Ordinal) >= 0
                || original.IndexOf(CommentMarkdown.End, end + CommentMarkdown.End.Length, StringComparison.Ordinal) >= 0)
                throw new IOException("已有评论段落标记不完整，请检查 Markdown 后重试。");
            updated = original[..start] + section.TrimEnd() + original[(end + CommentMarkdown.End.Length)..];
        }
        AtomicWrite(target, updated);
    }

    private static string SingleLine(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string Quote(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";

    private static bool IsValidWork(Work work)
    {
        var normalized = Works.FromUrl(work.Platform, Works.ParseUrl(work.Url), null, "");
        return Enum.IsDefined(work.Status) && Enum.IsDefined(work.CommentStatus) && work.CommentCount >= 0
            && normalized.Url == work.Url && normalized.Id == work.Id;
    }

    private static void AtomicWrite(string file, string text, Encoding? encoding = null, bool replace = true)
    {
        var temporary = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporary, text, encoding ?? new UTF8Encoding(false));
            File.Move(temporary, file, replace);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
