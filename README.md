# 视频转 Markdown

简体中文 · [English](README.en.md)

按博主和日期批量导出带时间戳的 Markdown 转录稿，支持 B 站、抖音和 YouTube。Windows 桌面端通过本机 VideoToNo 完成转录。

## 适合批量整理博主作品

逐条粘贴链接、等待转录、下载文件，几条视频做起来就容易重复。这里可以填入一次博主主页和日期范围，先看作品清单，勾选需要的条目，再交给队列逐条转录并保存。

- **一个主页导出一系列作品**。例如，收集某位博主近五个月的公开视频，筛完直接批量导出。YouTube 频道可采集普通视频和 Shorts。
- **多位博主分别建批次**。每批有自己的输出目录、作品清单和处理进度，适合分开归档和后续补录。一个批次对应一个平台，可为不同博主重复建批次。
- **每条作品单独留稿**。Markdown 保留标题、原视频链接、发布日期和转录时间戳，便于搜索、引用和回看原片。
- **批次可恢复**。可以处理完当前条后暂停，重新打开批次继续，或按清单重试失败项。手头已有链接时，也可以粘贴多条链接或导入 CSV。

## 下载与使用

1. 从 [GitHub Releases](https://github.com/han944763-sys/video-to-markdown/releases/latest) 下载 `VideoToMarkdown-v0.2.0-win-x64.zip`，解压并双击 `视频转Markdown.exe`。Windows 需要 Edge WebView2 Runtime，便携包已包含 .NET 运行时。
2. 安装并启动 [VideoToNo 1.4.7](https://github.com/like-attract/video-to-note/releases/tag/v1.4.7)。程序连接 `127.0.0.1:8000`，也可以点「启动 VideoToNo」选择本机 EXE。转录优先使用可用字幕，没有字幕时走本机语音识别，默认使用 `base` 模型。
3. 选择平台，输入博主主页，设置日期和输出目录，再点「采集主页」。日期按北京时间判断，包含首尾两天。也可按作品链接导入，CSV 需要 `url` 列，可加 `published_date`（`YYYY-MM-DD`）和 `title`。
4. 核对清单后点「开始 / 继续」。日期待确认的作品可手动勾选。输出目录包含 `批次状态.json`、`作品清单.csv` 和每条作品的 `转录稿/*.md`。恢复时用「打开批次」选择原状态文件。

平台可能要求登录或验证，可在内嵌的「平台页面」操作。出现「采集未完整」时，已找到的作品仍可转录，遗漏项可通过链接补录。YouTube 每个标签最多采集 25 页。对于依赖画面表达的作品，建议结合原片和时间戳使用转录稿。

## 从源码构建

在 Windows 上安装 .NET 10 SDK，在项目根目录执行：

```powershell
dotnet run --project Tests/VideoMarkdown.Tests.csproj -c Release
dotnet publish App/VideoMarkdown.App.csproj -c Release -r win-x64 --self-contained true -p:DebugType=none -o dist/VideoToMarkdown-win-x64
```

项目通过 [VideoToNo 的纯转录接口](https://github.com/like-attract/video-to-note/blob/v1.4.7/skills/video-to-note/SKILL.md) 生成转录稿。
