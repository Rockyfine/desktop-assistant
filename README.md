# 桌面助手

Windows 11 x64 中文桌面语音助手。支持按键说话、可选后台唤醒、系统朗读、六个可热切换的语音环节，以及本地会话和日程。

## 使用

1. 运行 `dist/DesktopAssistant-Setup-1.3.0.exe`，从开始菜单打开「桌面助手」。已有安装可直接升级，新用户使用独立的空白数据目录。
2. 从对话页的「配置模型」或「管理模型」打开模型设置。选择「ChatGPT 订阅」时，点击「Continue with ChatGPT」，在浏览器确认账号和方案用量授权。返回应用后，从该账号的模型列表中选择模型，可先测试，再点击「使用该模型」。
   选择「API Key / 本机 Ollama」时，保存 OpenAI 兼容服务的基础地址和模型名。用本机 Ollama 时填写 `http://127.0.0.1:11434/v1` 和已安装的模型名。
3. 在对话页左侧点击「说话」开始录音，仪表变为琥珀色。再点「结束录音」完成。底栏显示麦克风状态并提供暂停和恢复。后台唤醒可在设置中启用，默认唤醒词为「你好助手」。用「试读唤醒词」检查所填文字和语言。
4. 在「日程」直接添加安排，或核对并保存模型生成的草稿。应用运行期间会在开始前五分钟提醒。关闭窗口后仍可通过托盘使用，结束进程可选托盘「退出」。

语音识别随包提供并在本机处理。后台唤醒需要对应的 Windows 语音识别组件，朗读使用 Windows 已安装的音色。密钥按服务地址保存到 Windows 凭据管理器，历史和日程位于 `%LOCALAPPDATA%\DesktopAssistant.56ec080e`，卸载时保留。

### 应用更新

首次启用应用内更新时，先退出托盘里的旧版本，再运行 1.3.0 安装包。安装器会使用现有安装位置。本地对话、日程、设置和登录信息继续保存在各自原有位置。

此后的版本可在「设置 → 应用更新」或托盘的「应用更新」入口检查。发现新版后，点击「下载并重启」。应用校验更新包的大小和 SHA-256 后退出，更新器在原安装位置完成替换并重新打开应用。更新期间会等待正在运行的助手退出，避免程序文件被多个进程同时使用。

### ChatGPT 订阅

订阅接入参考 Pi AI 的账号授权流程，使用官方 Sign in with ChatGPT 和 Responses API。支持添加多个账号、切换模型及退出所选账号。浏览器回调失败时可将完整回调地址粘贴到设置页。账号凭据由 Windows DPAPI 保护，退出时会尝试撤销续期会话。应用会将当前会话文字发送到所选服务。模型和用量由 ChatGPT 账号及工作区授权决定，订阅请求失败时需要手动选择其他接入方式。[Pi AI 参考](https://github.com/earendil-works/pi/blob/main/packages/ai/README.md#oauth-providers) · [OpenAI 订阅接入](https://developers.openai.com/siwc/token-sharing-open-source)

## 构建和更名

Windows 构建需要 .NET 10 SDK、NSIS 3.11，以及 Vosk 的 `vosk-model-small-cn-0.22.zip`。[模型归档](https://alphacephei.com/vosk/models/vosk-model-small-cn-0.22.zip) · [NSIS 下载](https://nsis.sourceforge.io/Download)

```powershell
.\build.ps1 -ModelArchive 'C:\Downloads\vosk-model-small-cn-0.22.zip'
```

可以用 `-Sdk`、`-Nsis` 和 `-BrandIcon` 指定工具与 ICO 文件。脚本在 `dist` 生成安装包和独立的 `SoftGain-Plugin.zip`。修改 `branding.json` 的名称可更换安装界面和开始菜单品牌。运行后改显示名则只更新应用内显示、托盘和提醒，唤醒词在单独的设置中修改。

开发调试可通过环境变量 `DESKTOP_ASSISTANT_DATA_DIR` 指定隔离的测试目录。应用需要测试用模型时，可把解压的 `vosk-model-small-cn-0.22` 文件夹放到可执行文件旁的 `models` 目录。

可用 `dotnet run --project tests/Smoke -- <临时目录> <解压后的模型目录> <解压后的SoftGain目录>` 运行本地验收。语音测试使用系统合成的普通话音频，需要安装 Windows 简体中文音色。已安装简体中文识别器时也会检查自定义唤醒词。

### 发布更新

`branding.json` 中的 `releaseRepository` 指定应用查询的公开 GitHub 仓库。发布前递增 `version`，用 `build.ps1` 生成 Windows 安装包和 `dist/update-windows-x64.json`。安装包与清单必须来自同一次构建。将它们上传到对应的稳定版本 Release，标签格式为 `v<version>`。应用通过该仓库的最新稳定发布读取清单。预发布版本可单独标记为 Prerelease。

```powershell
gh release create v1.3.0 dist/DesktopAssistant-Setup-1.3.0.exe dist/update-windows-x64.json dist/SoftGain-Plugin.zip --latest --title '桌面助手 1.3.0'
```

插件协议见 [语音插件](docs/voice-plugins.md)，发行包的第三方依赖与许可见 [第三方组件](ThirdPartyNotices.md)。
