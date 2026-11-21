# Installation

## Windows Installer (MSI)

`WinNetworkManager-<version>-win-x64.msi` installs for all users in `C:\Program Files\WinNetworkManager` and adds a
Start menu entry. Installation needs administrator rights (Program Files); WinNetworkManager itself then runs as a
normal user.

- **Upgrade**: run the newer MSI; the previous version is replaced (installing an older version over a newer one is
  refused).
- **Silent install**: `msiexec /i WinNetworkManager-<version>-win-x64.msi /qn`
- **Uninstall**: *Settings → Apps → Installed apps → WinNetworkManager → Uninstall*, or
  `msiexec /x WinNetworkManager-<version>-win-x64.msi`. This removes every installed file, the Start menu entry, and the
  start-with-Windows entry of the user who uninstalls (if it was turned on).

## Portable ZIP

`WinNetworkManager-<version>-win-x64-portable.zip`: extract anywhere and run `WinNetworkManager.exe`. The `portable.txt`
file next to it turns on portable mode: settings, history and logs are kept in a `Data` folder next to the program
(the folder must be writable). Delete `portable.txt` to use the per-user folder instead. To remove the portable
version, turn off *Start when I sign in* (if you turned it on) and delete the folder.

Both packages are self-contained: the .NET runtime is included and nothing else needs to be installed.

## Data locations

| Mode | Folder |
|------|--------|
| Installed | `%LOCALAPPDATA%\WinNetworkManager` |
| Portable | `Data` next to `WinNetworkManager.exe` |
| Override (testing) | the folder in the `WNM_DATA_DIR` environment variable |

Contents: `settings.json`, `history.db` (+ `history.db-wal`), `logs\`. Uninstalling keeps this folder because it
belongs to you; delete it to remove every trace (or use *Clear history* and *Reset to defaults* first).

The per-process collector uses a kernel event tracing session named `WinNetworkManager-Collector-<logon session>`. It
stops when WinNetworkManager exits. If WinNetworkManager is terminated abnormally the session stays until the next
start of WinNetworkManager (which replaces it) or a reboot; to stop it manually from an elevated prompt:
`logman stop WinNetworkManager-Collector-1 -ets` (the number is your logon session, usually 1).

## Compatibility

| Windows | Architecture | Status |
|---------|--------------|--------|
| Windows 11 24H2 / 25H2 | x64 | Supported, tested (build 26200) |
| Windows 11 22H2 / 23H2 | x64 | Supported (same APIs), not tested in this release |
| Windows 10 22H2 | x64 | Supported (same APIs), not tested in this release |
| Windows 10 / 11 | ARM64 | Builds from the same sources (`publish.ps1 -Runtime win-arm64`); not tested on ARM64 hardware |
| 32-bit Windows, Windows Server | — | Not supported / not tested |

All Windows APIs used exist since Windows 8 or earlier. Requirements for per-process traffic: administrator rights for
the helper (or *Performance Log Users*), and the kernel providers `Microsoft-Windows-Kernel-Network` and
`Microsoft-Windows-Kernel-Process`, present on every supported version.
