# Background Terminal

A Windows desktop terminal overlay that sits behind normal application windows. It uses [CoreMeter](https://www.nuget.org/packages/CoreMeter/) to integrate the terminal window with the desktop.

![Video Sample](https://s7.gifyu.com/images/wallpaperdemoresultcutgif.gif)

## Requirements

- Windows 10 or later
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)
- A configured terminal process, such as `cmd.exe`, PowerShell, or Windows OpenSSH

## Controls

- **Process**: The terminal or shell executable launched by Background Terminal.
- **Activation Key Combination**: The global shortcut used to show or hide the terminal.
- **Font Color, Size, and Family**: Controls terminal text appearance. Font Family accepts installed Windows fonts such as `Consolas`.
- **Regex Filter**: Removes matching text from displayed output, including unwanted terminal control sequences.
- **Window Status, Position, and Size**: Controls whether the desktop terminal can be moved and where it appears.
- **Newline Triggers**: Changes the newline sequence after an exact command is entered, then restores it after the configured exit command.

Settings are stored per user under `%LocalAppData%\BackgroundTerminal`. The per-user installer places the application under `%LocalAppData%\Programs\BackgroundTerminal`, does not require elevation, and leaves user settings in place when the application is uninstalled.

## Build

Run the installer script from the repository root:

```powershell
dotnet restore .\Background-Terminal\Background-Terminal.csproj --runtime win-x64
pwsh -ExecutionPolicy Bypass -File .\Background-Terminal-Setup\BuildMsi.ps1 -NoRestore
```

If WiX v3.11 lives in a local bin directory instead of `PATH`, point the script at it explicitly:

```powershell
pwsh -ExecutionPolicy Bypass -File .\Background-Terminal-Setup\BuildMsi.ps1 -NoRestore -WixPath "C:\Program Files (x86)\WiX Toolset v3.11\bin"
```

The script publishes `Background-Terminal` for `win-x64`, reads the app project version from `Background-Terminal\Background-Terminal.csproj` for the MSI product version, regenerates `Background-Terminal-Setup\HarvestedFiles.wxs` from the publish output without `.pdb` files, and builds `Background-Terminal-Setup\Background_Terminal_Setup.msi` with WiX v3.11 `candle.exe` and `light.exe`.

If `light.exe` cannot run ICE validation because the Windows Installer service is unavailable in the build environment, rerun the script with `-SuppressValidation`.

Set `LAUNCHAFTERINSTALL=1` when installing if you want the app to launch after the MSI finishes.

## SSH Security

Background Terminal does not implement its own SSH protocol or host-key policy. To open an SSH session, configure the terminal process to use Windows OpenSSH (`ssh.exe`) or run `ssh` from the configured shell. Host-key verification, credentials, agent use, and known-hosts management are handled by OpenSSH and the user's terminal environment.

