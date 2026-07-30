# Caddy Tools Win

A small GUI tool for editing a Caddy / FrankenPHP `Caddyfile` on Windows 10/11,
with buttons to validate the config, format it, and reload the running server.

It runs on the **.NET Framework 4.8** that ships with Windows 10/11 — no extra
install is required.

## Requirements

- Windows 10 or Windows 11
- .NET Framework 4.8 (preinstalled on Win10/11)
- A `Caddyfile` together with `caddy.exe` or `frankenphp.exe` in one directory

## Build

Double-click `build.bat` (or run it from a command prompt). It uses the built-in
`csc.exe` compiler and produces `CaddyToolsWin.exe`.

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

To point the program at a different directory later, use **Open** (Ctrl+O).

## Menu / shortcuts

The five commands are top-level menus (no "File" parent):

| Action   | Shortcut | Description |
|----------|----------|-------------|
| Save     | Ctrl+S   | Write the editor to the Caddyfile (enabled only when changed). |
| Open     | Ctrl+O   | Re-select the Caddy / FrankenPHP directory. |
| Validate | Ctrl+V   | Run `validate --config <Caddyfile>`. |
| Format   | Ctrl+F   | Run `fmt --overwrite <Caddyfile>` (or `--config` for FrankenPHP) and reload the editor. |
| Reload   | Ctrl+R   | Restart the service (`net stop`/`net start`) or run `reload --config <Caddyfile>`. |

> Note: the editor uses a monospace font and the shortcuts override the text
> box defaults (e.g. Ctrl+V is *Validate*, not paste). Use the right-click menu
> or Shift+Insert to paste text.

## Notes

- Both `caddy.exe` and `frankenphp.exe` run the subcommands directly
  (`validate` / `fmt` / `reload`); `frankenphp` does **not** require a `caddy`
  prefix (i.e. `frankenphp validate`, not `frankenphp caddy validate`).
- `caddy fmt` does **not** accept a `--config` flag; the Caddyfile path is passed
  positionally (`fmt --overwrite "<path>"`). `frankenphp fmt` **does** support
  `--config`, so the tool uses `fmt --overwrite --config "<path>"` for FrankenPHP.
- When a service name is known, Reload elevates via UAC (administrator) to stop
  and start the service.
