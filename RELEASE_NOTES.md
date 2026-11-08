# iDeviceInstaller+Library Release

IPA library manager and installer for legacy iOS devices on Windows.

## v1.0.1

- Configurable queue row fields (status / note / name / version / installed size by default).
- Queue name now uses the library app name; version is shown once in its own field.
- Notes are shown for failed / paused / suspended tasks only.
- Force install for a single library row (skips minimum iOS and space checks when invoked from the context menu).
- Device apps view: multi-select, drag selection, batch uninstall and faster switching.
- Default scan root is `<program directory>\library`.
- English source now includes the same feature set as Chinese source.
- Version and About panel report 1.0.1.

## Supported devices

- Target: iOS 4 through iOS 10
- Tested: iOS 4.1, 6.1.3, 7.1.1, 8.4.1
- iPhone and iPad

## Packages

- `IdeviceGui-en-Windows.zip` - English UI
- `IdeviceGui-zh-Windows.zip` - Chinese UI

Each package includes the bundled idevice toolkit, license texts and
`SHA256SUMS.txt`.

## Notes

- This is an unofficial third-party tool. Not affiliated with Apple Inc.
- Use only with software you are legally allowed to use.
- No source code or test data is attached to this release; source is in the
  repository.
