# Third-party source snapshots

These directories contain source snapshots used to build the external idevice toolchain.
They were exported with `git archive HEAD` from the local MSYS2 working trees on
2026-11-08.

| Directory | Upstream | Commit | License |
|---|---|---|---|
| `libimobiledevice/` | libimobiledevice | `fa0f791` | LGPL-2.1 |
| `libimobiledevice-glue/` | libimobiledevice-glue | `10ddafa` | LGPL-2.1 |
| `libplist/` | libplist | `fe3dc34` (`2.8.0`) | GPL-2.0 / LGPL-2.1 |
| `libtatsu/` | libtatsu | `e7d6ad1` | LGPL-2.1 |
| `libusbmuxd/` | libusbmuxd | `93eb168` (`2.1.1-2`) | LGPL-2.1 |
| `ideviceinstaller/` | local ideviceinstaller fork | `d81675b` (`1.2.0-6`) | GPL-2.0 |

Each project keeps its own `COPYING` / `COPYING.LESSER` files.

Changes made for this repository:

- removed upstream `.github/` directories (no Actions are used here);
- removed the local `ideviceinstaller/FORK.md` because it contained developer-machine paths.

Not included:

- `libzip`;
- OpenSSL / GnuTLS / MbedTLS;
- prebuilt `.exe` / `.dll` binaries.

The local `ideviceinstaller` source includes Windows UTF-8/long-path handling, manifest
embedding, transfer progress and the UNC/2 GB fixes. See its git history upstream in
the local MSYS2 checkout for details.

## Removed test data

The upstream `libplist/test/` and `libplist/fuzz/` directories were removed
from this repository to keep it free of test data. This does not affect the
GUI or the bundled `toolkit/` binaries.
