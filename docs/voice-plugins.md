# 语音插件协议 v1

插件是具有标准输入和标准输出的 Windows x64 程序。用户选中本地 JSON 清单并确认信任后，就能在设置中切换相应引擎。插件进程以当前用户权限运行，每个正在使用的处理环节有自己的进程。请只选择可信插件。

```json
{
  "protocol": 1,
  "id": "soft-gain",
  "name": "柔和音量",
  "executable": "SoftGain.exe",
  "slots": ["fx"]
}
```

程序路径相对于清单目录，清单和程序内容的摘要变化后须重新在设置中确认。

## 消息

以 UTF-8 JSON Lines 发送请求和响应，每个请求须有一份响应并立即刷新标准输出。日志使用标准错误流。所有响应须带原请求的整数 `id` 和布尔字段 `ok`。音频是 Base64 编码的 16 kHz、16 位小端单声道 PCM，音频帧长 20 ms（640 字节）。单份响应上限为 2 MiB。

```json
{"id":1,"op":"hello","protocol":1,"slot":"fx","sampleRate":16000}
{"id":1,"ok":true,"protocol":1}
```

成功握手后宿主发出 `probe`。此时插件应检查是否可用，返回 `{"id":2,"ok":true}`。唤醒词 `keyword`、语言 `culture` 和系统音色 `speaker` 在适用时一并发送。所有插件也应回应 `reset`，并清理当前话段的状态。

| 环节 | 请求 | 响应字段 |
| --- | --- | --- |
| `aec` | `frame`，带麦克风 `audio`，播报时可带同长度的 `reference` | 等长 `audio` |
| `wake` | `frame`，带 `audio`、`keyword`、`culture` | 检测到唤醒时 `active: true` |
| `vad` | `frame`，带 `audio` | 有讲话时 `active: true` |
| `asr` | `reset` 开始话段，`frame` 接收麦克风音频，`finish` 完成 | `finish` 返回 `text` |
| `tts` | `synthesize`，带一段 `text` 和可选的 `speaker` | 一段完整的 PCM `audio` |
| `fx` | `effect`，带即将播放的 PCM `audio` | 等长 `audio` |

每帧响应预算为 300 ms，识别收尾为 3 秒，合成为 12 秒。音效同样须在 300 ms 内返回。新进程就绪后，识别在话段结束时切换，合成在该句播报完毕时切换，其余环节在下一次请求前切换。错误、意外退出或超时触发内置引擎。识别在话段中途失败时会提示重新说话。

## 示例

解压 `SoftGain-Plugin.zip`，在设置页加载 `plugin.json` 并把音效引擎切换成「柔和音量」。点击「试听音色」，声音会比内置直通模式更轻。可以随时切回「内置」。示例源码位于 `samples/SoftGain`。
