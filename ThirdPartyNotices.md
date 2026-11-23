# 第三方组件

桌面助手的程序、图标、界面和默认唤醒词在本项目中独立实现。安装包中的第三方组件适用各自的许可。随包的 `licenses` 目录保存许可原文，NuGet 精确版本与内容哈希见各项目的 `packages.lock.json`。

| 组件 | 许可与来源 |
| --- | --- |
| .NET、WPF、System.Speech、Microsoft.Data.Sqlite、ProtectedData | MIT。安装包包含 .NET 运行时及其第三方声明。[.NET](https://github.com/dotnet/runtime) · [WPF](https://github.com/dotnet/wpf) · [Data.Sqlite](https://github.com/dotnet/efcore) |
| NAudio 2.2.1 | MIT。[源代码与许可](https://github.com/naudio/NAudio/tree/v2.2.1) |
| Vosk 0.3.38 | Apache-2.0。使用官方 NuGet 中的 C# 库和 Windows 原生二进制。[源代码和构建过程](https://github.com/alphacep/vosk-api/tree/v0.3.38) · [NuGet](https://www.nuget.org/packages/Vosk/0.3.38) |
| Vosk 普通话模型 `vosk-model-small-cn-0.22` | 发布方标注 Apache-2.0。使用完整原始归档，含发布方 README。[模型目录与许可](https://alphacephei.com/vosk/models) |
| GCC 运行时 `libgcc_s_seh-1.dll`、`libstdc++-6.dll` | 由 Vosk NuGet 带入，适用 GPLv3 和 GCC Runtime Library Exception 3.1。这两个 DLL 的 SHA-256 与 Debian `8.3.0-6+21.3~deb10u2` 包中的 x86-64 POSIX 版本一致。对外发包时可随下载提供下列对应源码。[GCC 运行时许可](https://gcc.gnu.org/onlinedocs/libstdc++/manual/license.html) · [Vosk Windows 构建脚本](https://github.com/alphacep/vosk-api/tree/v0.3.38/travis) |
| Winpthreads `libwinpthread-1.dll` | 由 Vosk NuGet 带入，保留 MinGW-w64 的许可原文及 Lockless Inc. 条款。[Winpthreads 6.0.0 许可](https://github.com/mingw-w64/mingw-w64/blob/v6.0.0/mingw-w64-libraries/winpthreads/COPYING) |
| SQLitePCLRaw 和 SQLite | 包装器 Apache-2.0，SQLite 公有领域。归档附有 Apache-2.0 许可文本。[SQLitePCLRaw](https://github.com/ericsink/SQLitePCL.raw) · [SQLite 公有领域声明](https://www.sqlite.org/copyright.html) |
| NSIS | 安装程序工具链采用 zlib/libpng 风格许可，附带组件的许可见 NSIS 原始发行包。[NSIS 许可](https://nsis.sourceforge.io/License) |

Windows 唤醒识别器和朗读音色由用户的 Windows 安装提供。在线模型的服务条款由所配置的提供商决定。

## GCC 运行时对应源码

- Debian GCC 8.3.0-6。[源码清单](https://archive.debian.org/debian/pool/main/g/gcc-8/gcc-8_8.3.0-6.dsc) · [上游源码归档](https://archive.debian.org/debian/pool/main/g/gcc-8/gcc-8_8.3.0.orig.tar.gz) · [Debian 补丁](https://archive.debian.org/debian/pool/main/g/gcc-8/gcc-8_8.3.0-6.diff.gz)
- Debian MinGW-W64 交叉构建规则 21.3~deb10u2。[源码清单](https://archive.debian.org/debian/pool/main/g/gcc-mingw-w64/gcc-mingw-w64_21.3~deb10u2.dsc) · [构建规则归档](https://archive.debian.org/debian/pool/main/g/gcc-mingw-w64/gcc-mingw-w64_21.3~deb10u2.tar.xz)
