<p align="center"><img src="docs/logo.png" width="140"></p>
<h1 align="center">Rio Uninstaller</h1>
<p align="center">A fast, simple uninstaller for Windows with a modern dark UI (Arabic / English).</p>
<p align="center"><a href="https://7krh-hub.pages.dev/en/support/">☕ Buy me a coffee</a></p>

## Download
Get `RioUninstaller-Setup.exe` from the **Releases** page and run it.
Installed copies check this repository for new releases and show an update button when one is available.

## Features
Click a program, then pick one action:

| Action | What it does |
|---|---|
| **Standard uninstall** (default) | Closes the program, runs its uninstaller silently, then removes its program files, shortcuts, services, scheduled tasks, startup entries and install registry keys — **keeps your settings and data** (AppData / ProgramData / settings keys), so reinstalling brings everything back. |
| **Complete wipe** | Everything above, plus all of the program's data and settings: AppData, LocalLow, ProgramData, Temp folders and `HKCU/HKLM\Software\<Program>` keys — as if it was never installed. One click, no manual leftover selection. |
| **Stop background** | Kills the program's processes and disables its services, startup entries and scheduled tasks **without deleting anything**. Reversible with "Re-enable background". |

Shortcuts: `Enter` opens the selected program, `Delete` = standard uninstall, `Shift+Delete` = complete wipe.

## Safety
- Only removes items whose name exactly matches the program, or that point into the program's own folder.
- System and user folders (Windows, Program Files, AppData roots, Documents…) and shared publisher folders are never deleted.
- Any folder that contains another installed program is left alone.
- Sensitive registry areas (Microsoft, Classes, Policies, SYSTEM) are protected.

## No UAC prompt on every launch
The installer registers a scheduled task that starts Rio with admin rights, so Windows only asks once.

## Build
.NET 8 / WPF. GitHub Actions builds the app and the Inno Setup installer on every push to `main` and publishes a release.
