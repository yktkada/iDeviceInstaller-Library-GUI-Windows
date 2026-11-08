# iDeviceInstaller+Library · User Guide

> IPA library and installation queue for legacy iOS devices on Windows.
> This document describes the interface, the working logic, and the decision rules. It contains no walkthrough.

---

## 1. Overview

### 1.1 What it is

| Item | Value |
|---|---|
| Product name | `iDeviceInstaller+Library GUI For Windows` |
| Function | IPA library management, compatibility evaluation, multi-device installation queue |
| Implementation | C# / WPF / MVVM; calls `ideviceinstaller.exe` and `AfcHelper.exe` |
| UI language | Chinese and English builds |

An **IPA** is the installation package format of an iOS app. This program scans, parses, and installs such files.

### 1.2 Supported scope

| Item | Value |
|---|---|
| Devices | Verified: iPhone 4 (iOS 4.1), iPad 2 (6.1.3), iPhone 4S (7.1.1), iPhone 5 (8.1.2), iPod touch 5 (8.4.1) |
| iOS versions | 4 / 6 / 7 / 8 and iPad verified; **iOS 9 / 10 unverified** |
| Concurrency | Independent queue per device; four devices installing simultaneously verified |
| Library size | 423 packages / 97.8 GB verified; refresh baselines measured at 10,000 and 50,000 rows |

### 1.3 Requirements

| Item | Value |
|---|---|
| OS | Windows 10 / 11, 64-bit |
| iTunes | Required (provides Apple Mobile Device Service); no sign-in needed |
| Cable | Device connected over USB |
| Directory permission | Program directory must be writable; otherwise the program reports and exits at startup, without falling back to user directories |

---

## 2. Interface and Operations

![Main window](guide/01-main.png)

### 2.1 Regions

| Region | Content |
|---|---|
| Top bar | `Add...` `Install to...` `Hide log` `Settings` |
| Device tab strip | One tab per online device; wraps by window width; the leftmost fixed block shows the current run-state colour |
| Left · view switch | `Library` and `Device apps` as two equal-width buttons in fixed positions |
| Left · library toolbar | Multi-select, groups, scan, compatibility, advanced filter, merge |
| Left · grid | Library rows or device app rows; virtualized |
| Right · queue | Queue and progress of the current device tab; defaults to Status / Note / Name / Version / Installed size |
| Bottom right · log | Operation log; verbose/concise toggle; can be hidden |

### 2.2 Top bar

| Control | Function |
|---|---|
| `Add...` | Submenu: `Add folder...` scans a whole directory recursively; `Add files...` adds only the selected IPA files |
| `Install to...` | Installs the selected packages to devices. Menu contains all online devices, `More...` (device selection window), and a per-device `Install` submenu |
| `Hide log` | Collapses or expands the log pane (`Ctrl+L`) |
| `Settings` | Opens the settings window (8 tabs) |

### 2.3 Devices and queue

| Control | Function |
|---|---|
| `Refresh` | Re-probes devices immediately. Polling runs every 2 seconds by default; manual refresh is normally unnecessary |
| `Suspend` / `Resume` | One button whose caption follows the device run state: `Suspend` when Idle or Running; `Resume` when Paused, Suspended, or Offline |
| `Cancel` | Cancels the selected task. Cancelling during transfer counts as not installed; cancelling after the install stage begins uninstalls automatically when installation completes |
| `Clear` | Removes finished tasks and clears the red/yellow/green status marks in the library |

Queue rows default to:

```text
[status progress] | note | app name | version | installed size
```

Notes appear only for Failed / Paused / Suspended. Device apps have independent multi-select and batch uninstall.

### 2.4 Library toolbar

![Library toolbar](guide/02-library-toolbar.png)

| Control | Function |
|---|---|
| `Multi-select: off` / `Multi-select: N selected` | Toggles multi-select mode and shows the number of selected rows |
| Group dropdown | Switches between `All` and a user group. Empty groups are greyed out and cannot be selected |
| `Manage groups` | Creates, renames, and deletes groups |
| `Incremental scan` | Checks only new and changed files |
| `Full rescan` | Re-parses every present file (used to fill in size, icon, and architecture fields, or after parsing rules change) |
| Compatibility dropdown | `Compatibility filter: Off` / `Compatible with this device` / `Adapted to this device`; rules in 4.1 |
| Advanced filter switch + `Advanced filter (N)` | Condition filter: AND/OR within a group, AND between groups; supports version range, size range, multiple architectures, status, and compatibility |
| `Merge packages with the same BundleID` | Merges versions sharing a BundleId into a group row; group rows expand and collapse |

### 2.5 Grid

| Item | Content |
|---|---|
| Columns | Icon · Name · Uncompressed size · Version · Minimum iOS · Device family · BundleId · Architecture · Original size · Status (default order) |
| Column operations | Drag a column header to reorder; drag the edge to resize; both are persisted |
| Column visibility | Per-column switches under `Settings → Library` |
| Row height | Small (24) / Medium (32) / Large (40) |
| Group row | Appears when merging by BundleId; shows the version range and member count; collapsed by default |
| Check column | Marks whether the package is installed on the device; rules in 4.2 |

### 2.6 Log pane

| Control | Function |
|---|---|
| `Switch to detailed view` / `Switch to compact view` | Toggles log verbosity |
| `Export…` | Exports the current log |
| `Clear` | Clears the log pane |

### 2.7 Mouse gestures

| Gesture | Effect |
|---|---|
| Click a column header | Sorts by that column; clicking again toggles ascending/descending |
| Click a group row | Expands or collapses the group |
| Double-click a file row (two presses in the same column within 400 ms) | Adds to or removes from that device queue |
| Drag in multi-select mode | Toggles each row passed over, starting at the drag origin |
| Right-click a file row | Copy fields, group operations, `Install to...`, show/hide missing files, delete records for missing files |
| Right-click an unselected row | Selects the row first, then opens the menu |

### 2.8 Context menu

| Item | Function |
|---|---|
| `Copy BundleID` / `Copy name` / `Copy version` / `Copy minimum iOS` / `Copy entire row (tab-separated)` | Copies the corresponding field |
| `Add to group` / `New group` / `Manage groups` / `Remove from group` | Group operations; group rows do not show these |
| `Install to...` | Same menu as the top bar |
| `Select all` / `Clear all` | Multi-select mode only |
| `Show missing files` / `Hide missing files` | Toggles whether rows whose file is missing are shown |
| `Delete records for missing files` | Deletes all library records whose file is missing (no confirmation, does not delete files on disk) |
| `Add scan folder...` | Appends a scan root |
| `Force install to current device (skip minimum iOS and space check)` | Single IPA only; hidden in multi-select mode |
| `Clear` | Clears finished queue items and clears status marks |

---

## 3. Working Logic

### 3.1 Three stages

| Stage | Input | Output | Persistence |
|---|---|---|---|
| Scan | `.ipa` under the scan roots | Parsed rows | SQLite: `data\library.db` |
| Library | Parsed rows | Filtered, sorted, grouped view | View in memory only; groups written to `data\groups.json` |
| Queue | Library rows + target device | Install / uninstall results | Memory only; lost on close |

### 3.2 Two levels of state

**Device queue run state** (one per device):

| Caption | Meaning |
|---|---|
| Idle | No task executing |
| Running | The pump is executing tasks |
| Paused | Stopped for a device-level reason |
| Suspended | Stopped for a queue-level reason |
| Offline | Device unreachable |

**Task state** (one per task):

| Caption | Meaning |
|---|---|
| Waiting | Queued, not started |
| Verifying device | Compares `idevice_id -l` against `ProductType` / `ProductVersion` |
| Checking | Compatibility and file readability check; for uninstall only confirms the target exists |
| Cleaning | Clears `/PublicStaging` on the device |
| Checking space | Compares free space against required space |
| Installing / Uninstalling | `ideviceinstaller` executing |
| Reading back | Confirms the result via `list --xml` |
| Completed | All three checks pass; not written to the database, kept for this session only |
| Failed | Single task failed; the pump continues with the next task |
| Paused | Interrupted for a device-level reason; can be resumed |
| Suspended | Stopped at queue level; waiting to resume |
| Cancelled | Cancelled by the user |

### 3.3 Install sequence

```
Waiting → Verifying device → Checking → Cleaning → Checking space → Installing → Reading back → Completed
```

| Step | Result on failure |
|---|---|
| Verifying device | Offline / unreachable / identity mismatch → Paused |
| Checking | → Failed |
| Cleaning | Cleaning failure does not block (space hygiene is not an install prerequisite); only a fully unreachable AFC → Paused |
| Checking space | Insufficient space → Suspended (all waiting tasks on that device turn grey) |
| Installing | → Failed |
| Reading back | Not confirmed → Failed (exit code 0 with the app absent is not counted as success) |

The space check runs after cleaning: residual staging is removed first, then free space is evaluated.

### 3.4 Uninstall sequence

```
Waiting → Verifying device → Checking → Uninstalling → Reading back → Completed
```

For uninstall, the `Checking` step only confirms the target exists on the device. It performs no compatibility or space check and does not clean `/PublicStaging`.
Reason: on iOS 6 and 7, uninstalling an app that is not installed returns exit code 0; without reading back, "not installed" would be recorded as a successful uninstall.

### 3.5 Timeout criteria

| Call | Criterion | Result on timeout |
|---|---|---|
| `idevice_id -l` | 5 s | Device treated as offline |
| `ideviceinfo` probe | 15 s | Device treated as unresponsive |
| AFC helper (cleaning / space query) | 30 s total + 15 s without output | Process tree killed; device treated as unreachable; queue paused |
| `ideviceinstaller install` | 180 s without new output (not total duration) | Process tree killed, then the device is re-probed |
| `ideviceinstaller list --xml` | 30 s | Result unknown; recorded as failed |

### 3.6 Scanning and reconciliation

| Item | Content |
|---|---|
| Scan roots | Default `<program directory>\library`, created on first start; multiple recursive roots; added and removed under `Settings → Scan` |
| File identity | Volume serial + FileId preferred; path used as fallback |
| Incremental decision | If file identity, size, and modification time are unchanged, the existing parse result is reused |
| Failed rows | Rows that failed to parse are retried on the next scan |
| Write batches | The database is written once per 1000 parsed rows |
| During scanning | The library area is disabled and shows `Scanning library… (parsing N / M)`; the top bar remains usable except for the scan buttons |
| Startup order | Open database → apply layout → refresh devices → start 2-second polling → incremental scan |
| No early library read | Library content is built once after the scan completes |
| Full rescan | Re-parses all present files; `ParseState=Ok` does not imply that deep fields are complete |

### 3.7 Queue concurrency model

| Item | Content |
|---|---|
| Pump | One independent serial pump per device; tasks are serial within a device |
| Single-pump constraint | At most one pump per UDID; enforced by the pump registry |
| Locking | One lock per UDID; inside the lock only memory operations, no IO and no child processes |
| Between devices | No shared locks, no waiting on each other; an exception on one device stops only that device |
| Child processes | All run through the process runner; a child is considered hung when it produces no output; timeout and cancellation both kill the process tree |
| Window close | All child processes are terminated; no leftovers |

---

## 4. Decision Rules

### 4.1 Compatibility

| Dropdown item | Decision |
|---|---|
| `Compatibility filter: Off` | Not applied |
| `Compatible with this device` | The device family permits the device class, and `MinimumOSVersion` ≤ the device system version |
| `Adapted to this device` | Device family and `MinimumOSVersion` pass, and the package's highest tier for that device class ≥ the device tier |

| Item | Rule |
|---|---|
| Device tier source | `HardwareModel` preferred (case-sensitive); falls back to `ProductType` when unregistered |
| Mismatch between the two | `HardwareModel` wins; the status bar reports `Model identification mismatch (<ProductType>)` |
| Device family missing or `unknown` | Treated as the earliest iPhone (OG 3.5" / 320×480): `Compatible with this device` allows iPhone/iPod but not iPad; `Adapted to this device` allows nothing |
| Evidence source | Entirely from inside the IPA (`UILaunchImages`, storyboards, `Default*` resources, actual icon dimensions); no network access |

### 4.2 Installed markers

| Situation | Check column |
|---|---|
| Same BundleId, same version installed | Green background + check |
| Same BundleId, different version installed | White background + check |
| Not installed | White background, no check |
| Any member of a group row installed | The group row's check column turns green with a check; the whole row does not turn green |

The check column is not overridden by the row operation colour, selection colour, or double-click arm colour.
The installed set comes from `list --xml` readback; the database is not used and no extra scan is performed.

### 4.3 Queue ordering

Waiting tasks are ordered stably as follows:

```
1. Uninstall before install
2. Within the same kind, descending PeakBytes
   PeakBytes = compressed package size + uncompressed size
3. Within the same footprint, original enqueue order
```

| Item | Rule |
|---|---|
| Running task | Never preempted |
| Order consistency | The queue list and the device tab progress blocks use the same order |
| After resume | Re-sorted |

### 4.4 Space accounting

```
Available = TotalDataAvailable from ideviceinfo -q com.apple.disk_usage
            falls back to FSFreeBytes from the AFC helper when unavailable
Required  = peak footprint + 10 MB margin
Peak      = compressed package size + expected uncompressed size
            when uncompressed size is unknown: compressed package size × 2
```

| Timing | Behavior |
|---|---|
| Enqueue | No space check, no rejection, no state change |
| After each readback | Simulates all not-yet-started install tasks in queue order; if any task cannot fit, a log line is written and the queue is not modified (no rejection, removal, reordering, or pausing) |
| Log deduplication | Key is queue version + set of tasks that do not fit; identical results are not logged again |
| When an install task actually starts | Space is checked again after staging is cleaned; if insufficient, the device queue is suspended without uploading and without marking failure |

Uninstall tasks skip the space check. Running and finished tasks are not part of the simulation.

### 4.5 Success criteria

A task succeeds only when all three hold:

1. Exit code `== 0`
2. No line in `stderr` starting with `ERROR` (`WARNING` ignored)
3. Readback confirms: app present after install, app absent after uninstall

| Situation | Handling |
|---|---|
| Install reported an error but readback confirms installed | Recorded as completed |
| Exit code 0 but readback does not confirm | Recorded as failed |
| Uninstalling an app that is not installed (exit code 0 on iOS 6/7) | Caught by readback; recorded as failed |
| Readback retries | Up to 3 attempts, 1 s apart; exceptions, not-found, and version mismatch are all retried |
| Device offline | Does not consume a retry; the queue pauses immediately |
| Readback confirms installed but the version differs | The retry-suppression flag is set on that attempt; the task fails terminally and is not automatically re-queued |
| All 3 attempts exhausted | Exception → Failed (`Unable to verify result`); otherwise → Failed |

Readback retries apply only to the final success determination and to the pre-resume confirmation. Overwrite detection before installation and post-failure confirmation remain single readbacks.

### 4.6 Automatic behavior

| Behavior | Rule |
|---|---|
| Automatic re-queue on failure | `Settings → Devices → Automatic attempt count`, default 2, range 2–5, including the first attempt. When a task fails and the count has not been reached, the original entry is deleted and a new task is enqueued with the same parameters, noted `Attempt N`. Once the count is reached the task stays failed |
| Cases never re-queued | Readback confirmed installed but the version differs (prevents an overwrite install) |
| Suspend on disconnect | When a device-level pause occurs (offline or unreachable): the current task becomes Paused and is marked interrupted; all other waiting tasks become Suspended and are marked `SuspendedByDisconnect` |
| Auto-resume on reconnect | Requires `Auto-resume after stable connection` under `Settings → Devices`. The device must remain online for the whole stability window; a further disconnect within the window restarts the timer. Default window 3 s, range 1–10 s |
| Resume scope | Only tasks suspended due to disconnect and interrupted Paused tasks are resumed; tasks suspended manually by the user are left unchanged |
| Manual suspend | The current task is not interrupted and runs to a terminal state; all waiting tasks become Suspended; tasks enqueued while suspended enter directly as Suspended |

### 4.7 Routing of decision outcomes

| Stage | Situation | Outcome |
|---|---|---|
| Enqueue | No BundleId parsed | Rejected, not queued |
| Enqueue | Source file missing | Rejected, not queued |
| Enqueue | Lower than the package's minimum iOS | Rejected, not queued |
| Before execution | Offline / unreachable / identity mismatch | Paused |
| Before execution | Insufficient space | Suspended |
| Before execution | Package unreadable / minimum iOS recheck fails | Failed |
| During execution or readback | Otherwise | Failed |

`Waiting` means the task has not come up in the queue; it never stays in that state because a check failed.

---

## 5. Irreversible Operations and Data Loss

| Item | Description |
|---|---|
| Overwrite install | Installing over the same BundleId recreates the data container and clears the app's sandbox data. The program neither blocks nor confirms this |
| Queue and device list | Memory only. Closing the window or crashing loses the queue; the device list is re-probed |
| Delete records for missing files | Executes immediately without confirmation; deletes only library records, never files on disk |
| Cancel during installation | Cancelling after the install stage begins waits for installation to finish, then uninstalls automatically |
| Interrupted install | Before restarting after an interrupted transfer, a readback runs first; if already installed, the reinstall is skipped |
| Space pre-check | Advisory only; never modifies the queue |

---

## 6. Settings

![Settings window](guide/03-settings.png)

### 6.1 Library

| Item | Default / notes |
|---|---|
| Grid row height (DIP) | Small 24 / Medium 32 / Large 40 |
| Search delay threshold (rows) | Debouncing is used once the row count exceeds the threshold |
| Search delay (ms) | Debounce delay |
| Default sort | Saved only here; clicking a column header affects the current view only |
| Descending | Default sort direction |
| Search match mode | Contains / Exact |
| Search scope (columns) | Columns included in search; changing the current view does not write back to the default |
| Merge same BundleId | On by default |
| Show missing files | On by default |
| Use Retina text for device family (off = use R) | On by default |
| Column visibility | Per-column switches |

### 6.2 Scan

| Item | Notes |
|---|---|
| Scan roots (RootN) | Add with `Add folder…`, remove with `Delete selected` |
| Rows per batch write | One database write per N parsed rows |

### 6.3 Icons

| Item | Notes |
|---|---|
| Icon cache limit (MB) | Default 2048; icons still referenced are not evicted |
| Pick the smallest sufficient size for the current scale | Selects the smallest source image not smaller than the target |
| Downscale to cache bucket on disk | When off, original dimensions are stored and disk usage grows |

### 6.4 Log

| Item | Notes |
|---|---|
| Write file log `logs\app.log` | Off by default; `logs\crash.log` is always written |
| Log panel: verbose mode | Concise by default |

### 6.5 Layout

| Item | Notes |
|---|---|
| Hide log pane | Hidden state is remembered |
| Maximize log overlay width | Overlay width |
| Restore default three-pane proportions | Restores 45 / 27 / 28 |

### 6.6 Queue

| Item | Notes |
|---|---|
| Queue fields (order / visibility) | Status, Note, Name, Version, Installed size, BundleId, File path, Origin, Min iOS, Device family; use Move up/down |
| Default visible | Status, Note, Name, Version, Installed size |
| Note rule | Shown only for Failed / Paused / Suspended; an empty note takes no space |

### 6.7 Devices

| Item | Notes |
|---|---|
| Online poll interval (seconds) | Default 2 |
| Connection stability window (seconds) | Default 3, range 1–10 |
| Auto-resume after stable connection | On by default |
| Automatic attempt count | Default 2, range 2–5, including the first attempt |
| Device info fields (order / visibility) | `Move up` and `Move down` change the order |

### 6.8 About

Version number and product name. This tab is not affected by `Apply`, `Cancel`, or `Restore defaults`.

---

## 7. Data and Privacy

### 7.1 File locations

Everything lives under the program directory. Nothing is written to user directories, and `%APPDATA%` and `%TEMP%` are not used.

| Path | Content |
|---|---|
| `data\library.db` | Library (SQLite, WAL mode); scan roots, settings, and parsed package data |
| `data\groups.json` | User groups and member snapshots |
| `data\icons\` | Icon cache (`<sha1>-<bucket>.png`), bounded by the cache limit and reference protection |
| `data\deviceicons\` | Device app icons |
| `data\tmp\` | SQLite temporary files |
| `logs\app.log` | Runtime log, off by default; rotated at 5 MB × 3 |
| `logs\crash.log` | Crash log, overwritten, only the last crash kept |

### 7.2 Network

The program does not access the network. All information comes from local files, the USB device, and the command-line tools shipped with the program.
Compatibility evidence is derived from inside the IPA; no online lookups are made.

### 7.3 Unwritable directory

If the program directory is not writable, the program reports it at startup and exits; it does not fall back to another directory.

---

## 8. Known Limitations and Glossary

### 8.1 Limitations

| Item | Notes |
|---|---|
| iOS 9 / 10 | Unverified |
| System apps | Not shown in the device apps view; only user apps are listed |
| Architecture | The program does not block installation of an architecture mismatch; the behavior of armv6-only packages on armv7s devices is unverified |
| Device icons | Obtained from the SpringBoard icon interface; the number of icons available per session is limited, and failures are retried |
| Overwrite install | Reported as not preserving data, but not blocked |
| Forced install | Single IPA right-click `Force install to current device`: skips minimum iOS and space checks; unavailable in multi-select/group rows |
| Needs confirmation | `NeedsConfirm` is a reserved state and is not currently produced |
| Uninstall | Device apps support multi-select batch uninstall through the normal queue uninstall path, de-duplicated by BundleId |

### 8.2 Glossary

| Term | Meaning |
|---|---|
| IPA | iOS app installation package |
| BundleId | Unique identifier of an app |
| Device family | Device classes and screen tiers the package declares support for |
| Uncompressed size | Total uncompressed size of the `.app` inside the IPA |
| Original size | Size of the compressed IPA file |
| PeakBytes | Peak footprint = original size + uncompressed size |
| Readback | Reading the installed list from the device via `ideviceinstaller list --xml` |
| Pump | The serial task executor of one device |
| Suspend | Queue-level soft stop; tasks have not started |
| Pause | Task execution was interrupted; a readback is required before resuming |
| Scan root | A directory included in scanning |
