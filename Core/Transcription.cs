using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoMarkdown;

public sealed class VideoToNo(HttpClient http)
{
    public async Task<bool> Healthy(CancellationToken token)
    {
        try
        {
            var data = await Get("api/health", token);
            return data.Get("service").StringValue() == "VideoToNo" && data.Get("status").StringValue() == "ok"
                && data.Get("version").StringValue() == "1.4.7";
        }
        catch (HttpRequestException) { return false; }
        catch (JsonException) { return false; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return false; }
    }

    public async Task<string> Submit(Work work, IReadOnlyDictionary<string, string> cookies, CancellationToken token)
    {
        var request = new Dictionary<string, object>
        {
            ["video_url"] = work.Url,
            ["prefer_subtitles"] = true,
            ["whisper_model"] = "base",
            ["use_gpu"] = false,
            // VideoToNo 1.4.7's Bilibili cache ignores the selected parts.
            ["processing_mode"] = work.Platform == Platform.Bilibili ? "restart" : "reuse"
        };
        if (cookies.Count > 0 && work.Platform is Platform.Bilibili or Platform.Douyin)
            request[work.Platform == Platform.Bilibili ? "bilibili_cookie" : "douyin_cookie"] = cookies;
        using var response = await http.PostAsJsonAsync("api/transcribe", request, token);
        var result = await Read(response, token);
        var id = result.Get("task_id").StringValue();
        if (!Guid.TryParse(id, out _)) throw new JsonException("提交响应缺少有效任务 ID。");
        return id;
    }

    public Task<JsonElement> Status(string id, CancellationToken token) => Get(TaskPath(id), token);
    public Task<JsonElement> Transcript(string id, string format, CancellationToken token) =>
        Get($"{TaskPath(id)}/transcript?output_format={format}", token);

    private static string TaskPath(string id) => $"api/task/{Uri.EscapeDataString(id)}";

    private async Task<JsonElement> Get(string path, CancellationToken token)
    {
        using var response = await http.GetAsync(path, token);
        return await Read(response, token);
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, CancellationToken token)
    {
        if (!response.IsSuccessStatusCode)
        {
            var detail = "";
            try
            {
                using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                detail = error.RootElement.Get("detail").StringValue();
            }
            catch (JsonException) { }
            throw new HttpRequestException($"VideoToNo HTTP {(int)response.StatusCode} {SafeError(detail)}".Trim(), null, response.StatusCode);
        }
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
        return document.RootElement.Clone();
    }

    public static string SafeError(string value)
    {
        var redacted = Regex.Replace(value, @"https?://[^\s<>""']+|(?i:\b(?:cookie|authorization|sessdata|sessionid|sid_guard|msToken)\s*[:=]\s*[^\s,]+)", "[已省略]");
        return redacted.Length <= 400 ? redacted : redacted[..400];
    }
}

public sealed class TranscriptionQueue(VideoToNo service, TimeSpan pollInterval,
    Func<Work, Action<string>, CancellationToken, Task<CommentSnapshot>>? readComments = null)
{
    public static bool NeedsComments(Batch batch, Work work) => batch.IncludeComments && work.Selected
        && work.Status is WorkStatus.Succeeded or WorkStatus.NoTranscript && work.CommentStatus == CommentState.None;

    public async Task Run(Batch batch, BatchFiles files, Func<Platform, Task<IReadOnlyDictionary<string, string>>> credentials,
        Func<bool> pause, Action<string> update, CancellationToken token)
    {
        if (batch.Items.Any(work => work.Status == WorkStatus.Running
                || work.Selected && work.Status is WorkStatus.Ready or WorkStatus.ExportFailed)
            && !await service.Healthy(token))
        {
            update("VideoToNo 1.4.7 暂不可用。启动后点「继续」。");
            return;
        }
        foreach (var work in batch.Items.OrderByDescending(w => w.Status == WorkStatus.Running))
        {
            if (pause()) break;
            token.ThrowIfCancellationRequested();
            if (!NeedsComments(batch, work) && work.Status != WorkStatus.Running
                && (!work.Selected || work.Status is not (WorkStatus.Ready or WorkStatus.ExportFailed)))
                continue;
            if (work.Status == WorkStatus.Ready)
            {
                var cookies = await credentials(work.Platform);
                work.Status = WorkStatus.Submitting;
                work.Error = null;
                try { files.SaveState(batch); }
                catch
                {
                    work.Status = WorkStatus.Ready;
                    throw;
                }
                update($"正在提交 {work.DisplayTitle}");
                try
                {
                    work.TaskId = await service.Submit(work, cookies, token);
                    work.Status = WorkStatus.Running;
                }
                catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.TooManyRequests)
                {
                    work.Status = WorkStatus.Ready;
                    work.Error = "VideoToNo 正在忙。当前任务完成后点「继续」。";
                    files.Save(batch);
                    update(work.Error);
                    return;
                }
                catch (HttpRequestException error) when ((int?)error.StatusCode is >= 400 and < 500)
                {
                    work.Status = WorkStatus.Failed;
                    work.Error = error.Message;
                }
                catch (Exception error) when (error is HttpRequestException or OperationCanceledException or JsonException)
                {
                    work.Status = WorkStatus.SubmissionUnknown;
                    work.Error = "未收到任务 ID。确认 VideoToNo 未处理该条后，可手动重试。";
                    files.Save(batch);
                    update(work.Error);
                    return;
                }
                files.Save(batch);
            }
            if (work.Status is WorkStatus.Running or WorkStatus.ExportFailed)
            {
                try { await Retrieve(work, files, update, token); }
                catch (Exception error) when (error is HttpRequestException or JsonException
                    || (error is OperationCanceledException && !token.IsCancellationRequested))
                {
                    work.Error = "读取转录服务失败，队列已保留。连接恢复后点「继续」。";
                    files.Save(batch);
                    update(work.Error);
                    return;
                }
                files.Save(batch);
            }
            if (NeedsComments(batch, work)) await SaveComments(work, files, update, token);
            files.Save(batch);
            update($"{work.DisplayStatus} · {work.DisplayTitle}");
        }
        update(pause() ? "已暂停，点「继续」可恢复。" :
            $"本轮处理结束 · 已导出 {batch.Items.Count(w => w.Status == WorkStatus.Succeeded)} 条 · " +
            $"无有效转录 {batch.Items.Count(w => w.Status == WorkStatus.NoTranscript)} 条 · " +
            $"失败 {batch.Items.Count(w => w.Status is WorkStatus.Failed or WorkStatus.ExportFailed or WorkStatus.SubmissionUnknown)} 条" +
            (batch.IncludeComments ? $" · 评论已保存 {batch.Items.Count(w => w.CommentStatus is CommentState.Saved or CommentState.Partial)} 条视频" : ""));
    }

    private async Task SaveComments(Work work, BatchFiles files, Action<string> update, CancellationToken token)
    {
        try
        {
            if (readComments is null) throw new InvalidOperationException("评论采集器未就绪。");
            update($"正在保存评论 · {work.DisplayTitle}");
            var snapshot = await readComments(work, update, token);
            token.ThrowIfCancellationRequested();
            if (work.MarkdownPath is null && work.Status == WorkStatus.NoTranscript)
                work.MarkdownPath = files.Export(work, work.DisplayTitle, "本视频未取得有效的带时间戳转录。");
            files.WriteComments(work, snapshot);
            work.CommentStatus = snapshot.State;
            work.CommentCount = snapshot.Items.Count;
            work.CommentNotice = $"评论 {snapshot.Items.Count} 条 · {snapshot.Notice}";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException
            or JsonException or InvalidOperationException or System.Runtime.InteropServices.COMException
            || error is OperationCanceledException && !token.IsCancellationRequested)
        {
            work.CommentStatus = CommentState.Failed;
            work.CommentNotice = $"评论保存失败 · {VideoToNo.SafeError(error.Message)}";
        }
    }

    private async Task Retrieve(Work work, BatchFiles files, Action<string> update, CancellationToken token)
    {
        if (work.TaskId is not { } id || !Guid.TryParse(id, out _))
        {
            work.Status = WorkStatus.Failed;
            work.Error = "找不到有效的转录任务 ID。";
            return;
        }
        string? failure = null;
        while (work.Status == WorkStatus.Running)
        {
            JsonElement state;
            try { state = await service.Status(id, token); }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                failure = "VideoToNo 已找不到该任务。";
                break;
            }
            var status = state.Get("status").StringValue();
            if (status == "completed") break;
            if (status is "failed" or "cancelled" or "interrupted")
            {
                var detail = VideoToNo.SafeError(state.Get("error").StringValue());
                failure = detail.Length == 0 ? $"VideoToNo 任务状态为 {status}。" : detail;
                break;
            }
            if (status is not ("pending" or "processing" or "queued" or "running" or "cancelling"))
                throw new JsonException("转录服务返回未知状态。");
            update($"{work.DisplayTitle} · {state.Get("step_name").StringValue()} {state.Get("progress").StringValue()}%");
            await Task.Delay(pollInterval, token);
        }
        JsonElement json;
        try { json = await service.Transcript(id, "json", token); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            work.Status = failure?.Contains("没有从视频中获得可总结的文字", StringComparison.Ordinal) == true
                ? WorkStatus.NoTranscript : WorkStatus.Failed;
            work.Error = failure ?? "VideoToNo 没有可读取的转录产物。";
            return;
        }
        var segments = json.Get("segments");
        if (segments.ValueKind != JsonValueKind.Array)
            throw new JsonException("转录服务没有返回分段数组。");
        if (!segments.EnumerateArray().Any(ValidSegment))
        {
            work.Status = WorkStatus.NoTranscript;
            work.Error = "没有带有效时间段的转录正文。";
            return;
        }
        JsonElement markdown;
        try { markdown = await service.Transcript(id, "markdown", token); }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
        {
            work.Status = WorkStatus.Failed;
            work.Error = "VideoToNo 已返回分段，但缺少 Markdown 产物。可重新转录。";
            return;
        }
        var title = markdown.Get("title").StringValue();
        var text = markdown.Get("text").StringValue();
        if (string.IsNullOrWhiteSpace(text) || !Regex.IsMatch(text, @"\b\d{1,3}:\d{2}\b"))
        {
            work.Status = WorkStatus.NoTranscript;
            work.Error = "Markdown 缺少带时间戳的正文。";
            return;
        }
        try
        {
            work.MarkdownPath = files.Export(work, title, text);
            if (work.Title.Length == 0) work.Title = title;
            work.Status = WorkStatus.Succeeded;
            work.Error = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            work.Status = WorkStatus.ExportFailed;
            work.Error = error.Message;
        }
    }

    private static bool ValidSegment(JsonElement segment)
    {
        var start = segment.Get("start");
        var end = segment.Get("end");
        return start.ValueKind == JsonValueKind.Number && start.TryGetDouble(out var from) && from >= 0
            && end.ValueKind == JsonValueKind.Number && end.TryGetDouble(out var through) && through > from
            && !string.IsNullOrWhiteSpace(segment.Get("text").StringValue());
    }
}
