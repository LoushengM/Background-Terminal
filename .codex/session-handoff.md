# Codex Future Session Handoff

Last updated: 2026-06-16

This repo was already dirty when the cleanup work began. Treat the current
working tree as the user/work-in-progress baseline unless the user explicitly
asks to revert something.

## What Changed In This Cleanup

- Core reliability:
  - `Background-Terminal.Core/SettingsService.cs` quarantines corrupt JSON but
    does not overwrite settings for read/permission failures.
  - Settings saves use unique atomic staging files.
  - `BuiltInCommandParser` uses a small escape parser for `\r`, `\n`, `\t`,
    and `\\`; unknown or trailing escapes are rejected.
  - `TerminalOutputBuffer` trims old partial chunks instead of dropping too
    much recent output.
- Terminal sessions:
  - ConPTY cleanup now uses one explicit timeout policy and avoids fixed drain
    sleeps.
  - Redirected-process interrupt is documented/implemented as terminate-tree
    fallback. It is not a true console Ctrl+C.
  - `ITerminalSession.StartAsync` must stay source-compatible. Do not add
    parameters to it. Built-in sessions use the internal
    `IWorkingDirectoryTerminalSession` capability for working directory support.
- UI/windowing:
  - Lock/unlock no longer recreates the terminal session/window.
  - The user's older reload-on-lock behavior existed to repair a broken blinking
    block cursor after move/resize. Current fix is more targeted:
    `CursorAdorner` is rebuilt/refreshed after lock, move, resize, and layout
    changes.
  - Shutdown is intended to be single-path/non-reentrant.
  - Saved regex startup is guarded.
  - Alt-Tab style changes use pointer-sized Win32 style APIs.
  - Settings content is scrollable/DPI friendlier.
- Installer:
  - `Background-Terminal-Setup/BuildMsi.ps1` is the repo-local x64 MSI build
    script.
  - It publishes `win-x64`, regenerates `HarvestedFiles.wxs` from publish
    output, excludes `.pdb`, and builds
    `Background-Terminal-Setup/Background_Terminal_Setup.msi`.
  - `LAUNCHAFTERINSTALL=1` is optional. Default install does not launch the app.

## Commands That Passed

Run from repo root:

```powershell
dotnet build Background-Terminal\Background-Terminal.csproj -c Release --no-restore --property:Platform=x64
dotnet test Background-Terminal.Core.Tests\Background-Terminal.Core.Tests.csproj -c Release --no-restore
dotnet test Background-Terminal.IntegrationTests\Background-Terminal.IntegrationTests.csproj -c Release --no-restore --property:Platform=x64
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Background-Terminal-Setup\BuildMsi.ps1 -NoRestore -SuppressValidation
```

Expected test counts at handoff:

- Core tests: 36 passed
- Integration tests: 4 passed

## MSI Verification Notes

Local WiX v3.11 tools were available at:

```text
%TEMP%\codex-wix\wix.3.11.2\tools
```

`light.exe` ICE validation failed locally because the Windows Installer service
was unavailable, so the successful build used `-SuppressValidation`. The README
documents this.

When extracting MSI contents with `dark.exe`, run it from a temp directory or it
may leave a root-level `Background_Terminal_Setup.wxs` decompile byproduct.

Final verification after the last build:

- Packaged `Background-Terminal.exe` hash matched the publish output.
- Extracted MSI contained zero `.pdb` files.
- MSI SHA256:
  `3BE714475BBE80698826FEBDA8A33596B90E614865BB4320B5C6BAF303F52D62`

## Review Tripwires

- Re-check `ITerminalSession.cs` if terminal/session files change. Keep the
  public method signatures stable.
- Rebuild the MSI after any app code change; the installer embeds the published
  single-file exe.
- Keep WiX `HarvestedFiles.wxs` generated from publish output. Do not manually
  add debug symbols.
- Preserve the cursor repair path in `TerminalWindow`/`CursorAdorner`; a full
  terminal reload is no longer required for the lock transition.
- `git diff --check` currently emits only LF-to-CRLF normalization warnings in
  this checkout, no whitespace errors.

## Additional Oddities Worth Remembering

- This project targets `net10.0-windows`. If a future machine cannot build it,
  check the installed .NET SDK before changing project files.
- Integration tests should be run with `--property:Platform=x64`; otherwise the
  app/test build can drift into the wrong platform output.
- The app publish is single-file/self-contained. The MSI should package from the
  `publish` directory, not the adjacent build output directory.
- `Background-Terminal-Setup` contains generated WiX/build artifacts
  (`.wixobj`, `.wixpdb`, `.msi`) in the repo. Do not delete them as cleanup
  unless the user asks.
- `BuildMsi.ps1` sets `NUGET_SIGNATURE_VERIFICATION=false` for local publish
  reliability. If restore behavior changes, inspect that before blaming WiX.
- `TerminalWindow.xaml` now uses a `TextBox` with a custom cursor adorner, not
  the old text control behavior. Cursor fixes probably belong in
  `CursorAdorner` or the adorner refresh path, not by restarting the terminal.
- `MainWindow.UpdateNewlineTrigger` still has a UI-side `Regex.Unescape` helper.
  Core command newline parsing was fixed with a whitelist parser, but this UI
  trigger path may be a future cleanup target if newline behavior gets weird.
- If `git status` later shows only `.codex/` dirty, the implementation changes
  may already have been incorporated into the checkout. Still treat any current
  user changes as the baseline and do not reset them.
