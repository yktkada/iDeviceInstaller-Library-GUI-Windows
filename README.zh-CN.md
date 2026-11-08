# iDeviceInstaller+Library GUI For Windows

**适用于旧版 iOS 设备的 IPA 资料库与安装器**

[English](README.md) | 简体中文

一个用于管理本地 IPA 资料库，并向旧版 iOS 设备安装应用的 Windows 桌面程序。使用 C# / WPF / .NET 10 开发。

![平台](https://img.shields.io/badge/platform-Windows-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)
![iOS](https://img.shields.io/badge/iOS-4%E2%80%9310-000000)

> 非官方第三方工具，与 Apple Inc. 无关联。

---

## 仓库历史

公开历史已重写为单个根提交，之前的提交记录已删除，旧提交哈希不再有效。

## 概述

iDeviceInstaller+Library 面向旧版 iOS 设备和大规模本地 IPA 收藏，把三件事放在同一个程序里：

- 快速本地 IPA 资料库；
- 旧设备兼容性筛选；
- 每台设备独立安装队列。

项目提供两套源码：

| 源码目录 | 界面语言 | 工程 |
|---|---|---|
| `src/en` | English | `src/en/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj` |
| `src/zh` | 中文 | `src/zh/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj` |

两套代码结构一致，只有界面文案不同。

## 功能

- 扫描 IPA 文件夹，建立本地 SQLite 资料库。
- 读取 IPA 元数据：名称、BundleId、版本、最低 iOS、设备族、架构、包体积和来源。
- 支持搜索、排序、分组和筛选。
- 旧设备兼容性模式。
- 通过 USB 安装和卸载应用。
- 每台已连接设备独立安装队列。
- 空间检查、staging 清理、失败重试和回读确认。
- 多设备队列状态和进度显示。
- 日志面板导出。
- 中文和英文两套源码。

## 支持的设备

目标支持：

- iOS 4 到 iOS 10
- iPhone 和 iPad

已在以下系统实测：

- iOS 4.1
- iOS 6.1.3
- iOS 7.1.1
- iOS 8.4.1

iOS 9 和 iOS 10 是目标平台，但**尚未实测**。  
iOS 10 以上不官方支持。

程序依赖 `libimobiledevice` / `ideviceinstaller` 与目标设备的兼容性。重要数据请先自行验证。

## 构建要求

- Windows 10 或 Windows 11
- .NET SDK 10
- 无需另外下载 idevice 工具链

构建英文版：

```powershell
dotnet build src/en/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj -c Release
```

构建中文版：

```powershell
dotnet build src/zh/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj -c Release
```

## 工具链

仓库已包含 Windows x64 idevice 工具链：

```text
toolkit/
```

其中有 CLI 工具、运行时 DLL、许可证文本和 `SHA256SUMS.txt`。
GUI 构建会通过 `RepoToolkitDir` 自动使用仓库内工具链，不需要额外下载。

对应的库源码快照在 `third_party/`。
详见 `toolkit/README.md` 和 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

## 项目结构

```text
src/
  en/   英文源码
  zh/   中文源码

    IdeviceGui/prototype/IdeviceGui/         主 WPF 程序
    IdeviceGui/prototype/IdeviceIconHelper/ 图标 helper
    IdeviceGui/AfcHelper/                   AFC helper
  third_party/libimobiledevice/             上游 libimobiledevice 源码
```

## 第三方源码

仓库包含外部 idevice 工具链的源码快照：

```text
third_party/
  libimobiledevice/
  libimobiledevice-glue/
  libplist/
  libtatsu/
  libusbmuxd/
  ideviceinstaller/
```

各项目保留自己的许可证文件。GUI 本身采用 GPL-3.0-only，
第三方许可证见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

这些是源码快照，不是预编译二进制；`libzip` 和 SSL 库仍需另外提供。

## 状态

项目围绕真实旧设备流程开发，已在 iPhone 4 / 4S / 5 与 iPad 2 级别设备上验证。具体设备和 IPA 的兼容性不做保证。

## 免责声明

- 本工具为非官方工具，与 Apple Inc. 无关联。
- 请只使用你有权使用的 IPA。
- 在旧版 iOS 上覆盖安装可能清空应用沙盒数据。
- 不提供任何担保，使用风险自负。

## 许可证

本项目采用 **GNU General Public License v3.0**（GPL-3.0-only）授权。
详见 [LICENSE](LICENSE) 和 [COPYRIGHT](COPYRIGHT)。

第三方组件说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
