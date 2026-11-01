# FC 27 DLSS Switch – source code

Source code of the **FC 27 DLSS Switch** mod for EA SPORTS FC 27 (PC).
Mod page and download: https://www.nexusmods.com/easportsfc27/mods/246

> This mod was created entirely by **Claude Opus**, an AI model by Anthropic. I am only sharing it with the community.

## What it does

A small Windows tray tool. It switches the upscalers that are already built into FC 27 but hidden in the graphics menu:
NVIDIA DLSS Super Resolution / DLAA (through NVIDIA Streamline) and AMD FSR 3. Modes are changed in game with hotkeys
(Ctrl+F5 to Ctrl+F12).

It does this by changing three 4-byte values (upscaler mode, frame synthesis mode, frame generation off) in the game's
`WorldRenderSettings` objects in the game's memory.

## How it works (for reviewers)

- Everything is in one C# file, [`source/DlssSwitch.cs`](source/DlssSwitch.cs) (about 1,600 lines, .NET Framework 4, no third-party libraries).
- It opens `FC27.exe` with `OpenProcess` (`PROCESS_VM_READ | PROCESS_VM_WRITE | PROCESS_VM_OPERATION | PROCESS_QUERY_INFORMATION`).
- It reads the game's own type information (class, field and enum names) to find where the settings are. Then it searches
  the game's memory for those objects with `ReadProcessMemory` / `VirtualQueryEx`. Every object is checked field by field and
  looked at twice before anything is written.
- It writes only those three values with `WriteProcessMemory`, and checks the object again before every write.
- **What it does not do:**
  - no code injection: no DLL injection, no remote threads, no memory allocation inside the game
  - no network access, no downloads, no registry changes, no autostart
- The only files it writes are its own settings, log, state and cache files next to the EXE.
- It needs administrator rights (see `source/app.manifest`) to open the game process.
- Hotkeys use `RegisterHotKey`. The graphics card is detected through WMI (`Win32_VideoController`).
- `Install.bat` / `files/install.ps1` copy the NVIDIA Streamline / DLSS DLLs into the game folder and back up any existing
  files first. These DLLs are in the Nexus download only, unmodified and signed by NVIDIA, and their SHA-256 is checked.
  `Uninstall.bat` reverses this.
- **Why antivirus programs may flag it:** the EXE is unsigned, asks for administrator rights and writes into another
  process's memory. Game trainers behave the same way, so some scanners flag it based on behavior alone.

## Build it yourself

Double-click `source\Build.bat`. It compiles `FC27_DLSS_Switch.exe` with the C# compiler that ships with Windows
(`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`). No SDK or download is needed.
The EXE in the Nexus download was built from this source in the same way.

## Note

The switch can only access the game when FC 27 is started through the FC Live Editor and played offline.
The switch itself does not touch or bypass any anti-cheat. Online modes are not supported.

Unofficial fan project, not affiliated with EA, NVIDIA or AMD. NVIDIA, GeForce RTX, DLSS and Streamline are trademarks
of NVIDIA Corporation. AMD and FidelityFX are trademarks of Advanced Micro Devices, Inc.
