# Third-Party Notices

This repository contains third-party source under `third_party/` and the
corresponding Windows x64 runtime binaries under `toolkit/`.

## Binaries included in this repository

The `toolkit/` directory contains the runtime executables and DLLs built from
these sources. Each component keeps its upstream license text in
`toolkit/LICENSES/`.

## Source included in this repository

| Component | License | Location |
|---|---|---|
| libimobiledevice | LGPL-2.1 | `third_party/libimobiledevice/` |
| libimobiledevice-glue | LGPL-2.1 | `third_party/libimobiledevice-glue/` |
| libplist | GPL-2.0 / LGPL-2.1 | `third_party/libplist/` |
| libtatsu | LGPL-2.1 | `third_party/libtatsu/` |
| libusbmuxd | LGPL-2.1 | `third_party/libusbmuxd/` |
| ideviceinstaller | GPL-2.0 | `third_party/ideviceinstaller/` |

The snapshots are exported from the local MSYS2 source trees. Upstream `.github/`
directories were removed, and the local `ideviceinstaller/FORK.md` was removed
because it contained developer-machine paths. See `third_party/README.md`.

## Runtime libraries in `toolkit/`

The bundled toolkit also contains these runtime libraries:

| Component | License | License text |
|---|---|---|
| OpenSSL (`libssl`, `libcrypto`) | Apache-2.0 | `toolkit/LICENSES/openssl-LICENSE` |
| libzip | BSD-3-Clause | `toolkit/LICENSES/libzip-LICENSE` |
| bzip2 | bzip2 license | `toolkit/LICENSES/bzip2-LICENSE` |
| xz/liblzma | Public Domain / 0BSD / LGPL-2.1 / GPL | `toolkit/LICENSES/xz-*` |
| zstd | BSD-3-Clause | `toolkit/LICENSES/zstd-LICENSE` |
| zlib | zlib license | `toolkit/LICENSES/zlib-LICENSE` |

The toolkit is a collection of separate executables and dynamically loaded
libraries. It is not relicensed as GPL-3.0 as a whole; each component keeps
its own license.

## Build-time dependencies

| Component | License |
|---|---|
| .NET / WPF | MIT |
| Microsoft.Data.Sqlite | MIT |

See the package metadata for the exact license text of each NuGet package.

## Our source

The GUI source in this repository is licensed under GPL-3.0-only. See
[LICENSE](LICENSE) and [COPYRIGHT](COPYRIGHT).
