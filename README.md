# Caddy Tools Win

A small GUI tool for editing a Caddy / FrankenPHP `Caddyfile` on Windows 10/11,
with buttons to validate the config, format it, and reload the running server.

It runs on the **.NET Framework 4.8** that ships with Windows 10/11 — no extra
runtime install is required (the only bundled dependency is the AvalonEdit
editor control, shipped as `ICSharpCode.AvalonEdit.dll` next to the exe).

## Features

- Syntax highlighting for the Caddyfile (directives, comments, strings,
  placeholders) using AvalonEdit.
- Five top-level menu commands (no nested "File" menu).
- Single-instance: launching the program again when it is already running does
  nothing (no second window).
- Remembers the last used Caddy / FrankenPHP directory in
  `%USERPROFILE%\.caddy-tools-win`.

## Requirements

- Windows 10 or Windows 11
- .NET Framework 4.8 (preinstalled on Win10/11)
- A `Caddyfile` together with `caddy.exe` or `frankenphp.exe` in one directory

## Download / Install

Two packages are produced by the CI for each release:

| Package | File | Use |
|---------|------|-----|
| Portable | `CaddyToolsWin-<ver>-portable-win64.zip` | Unzip anywhere and run `CaddyToolsWin.exe`. No installation. |
| Setup | `CaddyToolsWin-<ver>-setup-win64.msi` | Double-click to install system-wide (Program Files + Start Menu shortcut). Requires admin. |

Both contain the same `CaddyToolsWin.exe` and the `ICSharpCode.AvalonEdit.dll`
it depends on.

## Build from source

Double-click `build.bat` (or run it from a command prompt). It uses the
built-in `csc.exe` compiler, copies `ICSharpCode.AvalonEdit.dll` next to the
exe, and produces `CaddyToolsWin.exe`.

## First run

On first launch the program asks you to pick the Caddy / FrankenPHP directory.
It then checks that:

1. a `Caddyfile` exists in that directory, and
2. either `caddy.exe` or `frankenphp.exe` exists there.

If a Windows service's executable path references that directory (for example a
WinSW service whose `-config` points at `<dir>\something.xml`), its service name
is remembered too — so the **Reload** action can restart it. If no service is
found, the service name is left empty and Reload falls back to `caddy reload`.

Settings are stored in `%USERPROFILE%\.caddy-tools-win` (a JSON file):

```json
{
  "caddyDir": "D:\\Tools\\frankenphp",
  "exeName": "caddy.exe",
  "serviceName": "frankenphp"
}
```

To point the program at a different directory later, use **Open**.

## Menu / shortcuts

The five commands are top-level menus (no "File" parent):

| Action   | Shortcut | Description |
|----------|----------|-------------|
| Save     | Ctrl+S   | Write the editor to the Caddyfile (enabled only when changed). |
| Open     | —        | Re-select the Caddy / FrankenPHP directory. |
| Validate | —        | Run `validate --config <Caddyfile>`. |
| Format   | —        | Run `fmt --overwrite <Caddyfile>` (or `--config` for FrankenPHP) and reload the editor. |
| Reload   | —        | Restart the service (`net stop`/`net start`) or run `reload --config <Caddyfile>`. |

Only **Save** has a keyboard shortcut (Ctrl+S). The other four are menu-only.

## Notes

- Both `caddy.exe` and `frankenphp.exe` run the subcommands directly
  (`validate` / `fmt` / `reload`); `frankenphp` does **not** require a `caddy`
  prefix (i.e. `frankenphp validate`, not `frankenphp caddy validate`).
  `--config`, so the tool uses `fmt --overwrite --config "<path>"` for FrankenPHP.
- When a service name is known, Reload elevates via UAC (administrator) to stop
  and start the service.
