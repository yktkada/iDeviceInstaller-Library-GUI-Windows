# Bundled idevice toolkit

Windows x64 runtime toolkit bundled with the GUI.

- `ideviceinstaller.exe` is the local fork with Windows UTF-8/long-path support, UNC and >2 GB transfer fixes, and the embedded long-path manifest.
- Binaries were built in MSYS2 MINGW64 and stripped before being added here.
- `SHA256SUMS.txt` contains the SHA256 of every shipped binary.

## Source / license

Corresponding source snapshots are in `../third_party/`.

| Component | Commit | License |
|---|---|---|
| libimobiledevice | `fa0f791` | LGPL-2.1 |
| libimobiledevice-glue | `10ddafa` | LGPL-2.1 |
| libplist | `fe3dc34` (2.8.0) | GPL-2.0 / LGPL-2.1 |
| libtatsu | `e7d6ad1` (1.0.5) | LGPL-2.1 |
| libusbmuxd | `93eb168` (2.1.1) | LGPL-2.1 |
| ideviceinstaller | `d81675b` (1.2.0 local fork) | GPL-2.0 |

License texts are in `LICENSES/`, including OpenSSL, libzip, bzip2, xz,
zstd, and zlib. See also `../THIRD_PARTY_NOTICES.md`.

The toolkit is an aggregate of independent executables and DLLs; the GPL-3.0
license of the GUI does not replace the licenses of the bundled third-party
runtime libraries.

## Runtime files

The GUI copies the toolkit DLLs and its four required command-line tools to its output directory.
Other `idevice*.exe` tools are included for completeness and manual repair/debug use.
