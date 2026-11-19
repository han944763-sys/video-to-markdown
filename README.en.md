# Video to Markdown

[简体中文](README.md) · English

Batch-export timestamped Markdown transcripts by creator and date range from Bilibili, Douyin, and YouTube. The Windows desktop app sends transcription jobs to a local VideoToNo instance.

## Why use it for a batch?

Pasting links, waiting for transcription, and downloading each result takes repetitive work when you want a whole series. Enter a creator’s channel and a date range once, review the videos found, and send the selected items to the transcription queue.

- **Export a series from one channel.** Collect a creator’s public uploads from the past five months, for example, then export the selected videos as a batch. YouTube collection covers regular videos and Shorts.
- **Keep separate batches for multiple creators.** Each batch has its own output folder, video list, and progress, so you can archive and update creators separately. Each batch uses one platform.
- **Get one Markdown file per video.** Files include the title, original link, publication date, and transcript timestamps for searching, quoting, and jumping back to the source.
- **Resume a batch.** Pause after the current item, reopen a saved batch, or retry failed items. You can also paste several video links or import a CSV when you already have a list.
- **Optionally keep comments.** Check **同时保存评论** to append comments and loaded replies to the transcript's Markdown file.

## Download and use

1. Download `VideoToMarkdown-v0.3.0-win-x64.zip` from [GitHub Releases](https://github.com/han944763-sys/video-to-markdown/releases/latest). Extract it and run `视频转Markdown.exe`. Windows requires the Edge WebView2 Runtime. The ZIP includes the .NET runtime.
2. Start [VideoToNo 1.4.7](https://github.com/like-attract/video-to-note/releases/tag/v1.4.7). The desktop app connects to `127.0.0.1:8000`. You can also use **启动 VideoToNo** to choose its EXE. Transcription uses available subtitles first, then falls back to local speech recognition with the `base` model.
3. Select a platform, enter the creator’s channel, set the dates and output folder, then click **采集主页**. Dates are inclusive in Beijing time (UTC+8). For CSV import, include a `url` column and optionally `published_date` (`YYYY-MM-DD`) and `title`.
4. Review the list and click **开始 / 继续**. Select any videos with unconfirmed dates manually. Each batch contains `批次状态.json` (batch state), `作品清单.csv` (video list), and one Markdown file per video under `转录稿/`. To resume, choose the original state file using **打开批次**.

Some platforms may require sign-in or verification in the embedded browser. If collection is incomplete, you can still process the videos already found and add missing links. YouTube collection reads up to 25 pages per tab. For videos that rely on visuals, use the timestamps to check the original footage alongside the transcript.

The comment option is unchecked by default. When enabled, the app opens each video page and saves up to 200 comments in loading order, including author, time, likes, and loaded replies. The comments section records the capture time, count, and any incomplete loading. Enable the option in an existing batch and click **开始 / 继续** to add comments to exported transcripts. Use **重试失败项** to retry failed or partial comment collection.

## Build from source

Install the .NET 10 SDK on Windows and run these commands from the repository root:

```powershell
dotnet run --project Tests/VideoMarkdown.Tests.csproj -c Release
dotnet publish App/VideoMarkdown.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist/VideoToMarkdown-win-x64
```

Transcription uses [VideoToNo’s transcription-only API](https://github.com/like-attract/video-to-note/blob/v1.4.7/skills/video-to-note/SKILL.md).
