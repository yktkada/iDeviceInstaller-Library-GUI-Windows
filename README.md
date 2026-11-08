# iDeviceInstaller+Library GUI For Windows

**IPA Library & Installer for Legacy iOS Devices**

For jailbroken legacy iOS devices with AppSync Unified. No signing, no sideloading, no unnecessary setup.

[English](README.md) | [简体中文](README.zh-CN.md)

A Windows desktop app for managing a local IPA library and installing apps on older iOS devices. Built with C# / WPF / .NET 10.

![Platform](https://img.shields.io/badge/platform-Windows-0078D4)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)
![iOS](https://img.shields.io/badge/iOS-4%E2%80%9310-000000)

> Unofficial third-party tool. Not affiliated with Apple Inc.

---

## Repository history

The published history was rewritten to a single root commit. Previous commit
records were intentionally removed. Old commit hashes are no longer valid.

## Overview

iDeviceInstaller+Library is built for legacy iOS devices and large local IPA collections. It combines three things in one app:

- a fast local IPA library;
- compatibility filtering for old devices;
- independent per-device install queues.

The GUI is available in two source trees:

| Source | UI language | Project |
|---|---|---|
| `src/en` | English | `src/en/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj` |
| `src/zh` | Chinese | `src/zh/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj` |

Both trees share the same structure and behavior; only user-facing strings differ.

## Features

- Scan an IPA folder and keep a local SQLite library.
- Read IPA metadata: name, BundleId, version, minimum iOS, device family, architecture, package size and origin.
- Search, sort, group and filter large libraries.
- Compatibility mode for older devices.
- Install and uninstall apps over USB.
- Independent install queue per connected device.
- Space check, staging cleanup, retry and readback verification.
- Multi-device queue view with progress per device.
- Exportable log panel.
- English and Chinese source editions.

## Supported devices

Officially targeted at:

- iOS 4 through iOS 10
- iPhone and iPad

Tested on iOS 4.1, 6.1.3, 7.1.1 and 8.4.1.

iOS 9 and iOS 10 are target platforms but have not been tested yet. iOS versions newer than 10 are not officially supported.

The app depends on `libimobiledevice` / `ideviceinstaller` compatibility with the target device. Test on your own hardware before relying on it for important data.

## Build requirements

- Windows 10 or Windows 11
- .NET SDK 10
- No separate toolkit download is required for this repository

Build the English edition:

```powershell
dotnet build src/en/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj -c Release
```

Build the Chinese edition:

```powershell
dotnet build src/zh/IdeviceGui/prototype/IdeviceGui/IdeviceGui.csproj -c Release
```

## Toolkit

A Windows x64 idevice toolkit is bundled in:

```text
toolkit/
```

It includes the CLI tools, runtime DLLs, license texts and `SHA256SUMS.txt`.
The GUI build automatically picks it up through `RepoToolkitDir`; no external
toolkit download is needed.

The source snapshots corresponding to the bundled libraries are in
`third_party/`. See `toolkit/README.md` and
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

## Project layout

```text
src/
  en/   English source
  zh/   Chinese source

    IdeviceGui/prototype/IdeviceGui/        main WPF app
    IdeviceGui/prototype/IdeviceIconHelper/ icon helper
    IdeviceGui/AfcHelper/                   AFC helper
  third_party/libimobiledevice/             upstream libimobiledevice source
```

## Third-party source

The repository includes source snapshots of the external idevice toolchain under:

```text
third_party/
  libimobiledevice/
  libimobiledevice-glue/
  libplist/
  libtatsu/
  libusbmuxd/
  ideviceinstaller/
```

Each project keeps its own license files. The GUI itself is GPL-3.0-only;
the dependency licenses are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

These are source snapshots, not prebuilt binaries. `libzip` and an SSL library
are still external build dependencies.

## Status

The project is developed around a real legacy-device workflow and has been exercised with iPhone 4 / 4S / 5 and iPad 2 class hardware. Compatibility with a specific device and IPA is not guaranteed.

## Disclaimer

- This is an unofficial tool and is not affiliated with Apple Inc.
- Use only with IPAs you are legally allowed to use.
- Installing or overwriting an app may clear its sandbox data on legacy iOS.
- No warranty is provided; use at your own risk.

## License

This project is licensed under the **GNU General Public License v3.0**
(GPL-3.0-only). See [LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).

Third-party notices are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
