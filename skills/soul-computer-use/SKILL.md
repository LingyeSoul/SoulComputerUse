---
name: soul-computer-use
description: "Install, register, and operate SoulComputerUse on Windows 11: a GUI host that opens a same-account Windows child session, plus the scu CLI and a local MCP stdio server exposing screenshot, mouse, and keyboard tools. Use when asked to install or download SoulComputerUse, add its MCP server to an agent, drive a sandboxed Windows desktop, or diagnose its E_* session errors."
---

# SoulComputerUse

SoulComputerUse opens a Windows child session on the same account, shows it in a desktop window, and exposes that child desktop through a local CLI (`scu`) and an MCP stdio server. The Windows login screen is completed by the human; the product and its child-session agent never receive credentials.

Installing this skill only teaches an agent how to install and operate the product. It does not install the application.

## Non-negotiables

- Never ask for, type, inject, or store a password, PIN, Hello gesture, or token. If the session is `awaiting-credentials`, stop and let the human log in inside the product window.
- Never automate the UAC secure desktop. `blocked` means stop and ask the human to finish the prompt.
- Treat screenshots as sensitive. Write them only to a path the user asked for and delete temporary files afterwards.
- Keep all five executables in the same directory. The host derives the agent and elevation helper paths from its own directory and refuses to fall back to `PATH`.
- One session owner per machine. Do not kill another SoulComputerUse process to take the global lock.

## 1. Locate or install the application

Check for an existing install first:

```powershell
$InstallRoot = Join-Path $env:ProgramFiles 'SoulComputerUse'
Test-Path (Join-Path $InstallRoot 'SoulComputerUse.Host.exe')
```

If it is missing, install the latest release with the script in this skill's `scripts` directory, from a normal-privilege PowerShell:

```powershell
pwsh -NoProfile -File <skill-dir>\scripts\install.ps1 `
  -Repository LingyeSoul/SoulComputerUse `
  -Version latest `
  -Destination $InstallRoot
```

The script downloads and verifies the release in that normal-privilege shell, then requests a single UAC prompt; the elevated phase only re-verifies the payload and copies it into Program Files. It refuses to start the download phase in an elevated shell. If post-copy verification or the CLI self-check fails, it removes the partial installation so a retry is not blocked. Run `start` and the CLI commands from a normal-privilege terminal too, because `start` refuses an elevated caller.

The script downloads the release asset and `SHA256SUMS.txt`, verifies the archive hash, expands the archive, verifies `install-manifest.json` against the extracted files and again against the installed copy, and prints the installed paths as JSON. It never edits `PATH`, the registry, or an agent configuration.

If the release is unavailable, use `references/install.md` for the manual download and verification steps, or `references/build-from-source.md` to publish from source.

## 2. Register the MCP server (optional)

The MCP server is a stdio process and a *client* of the session owner. It stays alive without a session, and opens the product window only when the agent calls `start_session`.

Register this command with the agent client:

```text
<InstallRoot>\SoulComputerUse.Mcp.exe
```

Examples:

```powershell
# Claude Code
claude mcp add --scope user soul-computer-use -- "$InstallRoot\SoulComputerUse.Mcp.exe"

# Codex
codex mcp add soul-computer-use -- "$InstallRoot\SoulComputerUse.Mcp.exe"
```

For OpenCode, add a local MCP entry to `opencode.json`:

```json
{
  "mcp": {
    "soul-computer-use": {
      "type": "local",
      "command": ["C:\\path\\to\\SoulComputerUse.Mcp.exe"],
      "enabled": true
    }
  }
}
```

See `references/install.md` for the full registration and smoke-test sequence.

## 3. Start a session

Start the session through the product first:

```powershell
& (Join-Path $InstallRoot 'SoulComputerUse.Cli.exe') start
```

With the MCP server registered, call the `start_session` tool instead; it performs the same product action as `scu start`.

`start` prints `ok` as soon as the host takes the machine lock. It does **not** wait for logon. The human completes the Windows login by hand in the product window; never type anything into it. Then poll:

```powershell
& (Join-Path $InstallRoot 'SoulComputerUse.Cli.exe') status
```

- `state=starting` or `state=connecting`: keep waiting.
- `state=awaiting-credentials`: tell the user to log in in the product window. Do not type anything.
- `state=ready`: proceed.
- `state=blocked`: stop; a UAC/secure-desktop prompt needs the human.
- `state=disconnected`: stop; the user must close and restart the host.

While the host is starting and while the human logs on, every session call answers `E_NOT_READY` with `retryable: true`. That is the normal wait, not an error: wait a moment and poll again.

`E_HOST_LAUNCH_FAILED` is not retryable. It means the host could not be launched from this environment: the agent is elevated, `SoulComputerUse.Host.exe` is missing, the caller is not on the interactive console session, process creation was refused, the host exited before taking the lock, or the wait limit passed. Read the message, fix the named cause, and only then retry; an elevated agent cannot start a session, so the human must run `scu start` from a normal-privilege terminal. Do not repeat `start` while the cause stands.

Do not enable the headless owner path (`SOULCOMPUTERUSE_ALLOW_HEADLESS`) in this build; `start` is the supported way to open a session.

## 4. Operate the child desktop

Use MCP tools when the client is connected; otherwise use the CLI with the full path:

```powershell
$scu = Join-Path $InstallRoot 'SoulComputerUse.Cli.exe'
& $scu status
& $scu shot --out (Join-Path $env:TEMP 'soul-computer-use.png')
& $scu move 640 360
& $scu click 640 360 --button left --count 1
& $scu scroll 640 360 0 -3
& $scu type "hello"
& $scu key ctrl+s
```

Coordinates are the child session's native physical pixels, origin at the top-left. Read `screen=<width>x<height>` from `status` and keep `0 <= x < width`, `0 <= y < height`.

MCP tool names are `get_status`, `screenshot`, `mouse_move`, `mouse_click`, `mouse_scroll`, `key_type`, and `key_press`. See `references/usage.md` for schemas, state rules, and error handling.

## 5. Close the session

Ask the user to close the product window and confirm "结束会话". The host must run its teardown: log off the child session, restore the child-session switch if this run enabled it, and release the machine lock. Do not kill the host process and do not log off the child session from outside the product.

## Troubleshooting

| Symptom | Action |
|---|---|
| `E_NOT_READY` | Call `scu start` / `start_session` first, then poll `status`. During startup and the logon wait this is the normal retryable answer: wait and poll again. |
| `E_HOST_LAUNCH_FAILED` | The host could not be launched: elevated agent, missing host executable, caller off the console session, or the host exited before the lock. Read the message and fix the named cause; an elevated agent needs the human to run `scu start` with normal privilege. |
| `E_LOCK_HELD` | Another owner exists. Use it or close it through its window; do not kill it. |
| `E_AWAITING_CREDENTIALS` | The human must log in in the product window. |
| `E_BLOCKED` | UAC or a secure desktop is up; stop and let the human finish it. |
| `E_RDP_INBOUND_DENIED` / `E_CHILD_CONNECTIONS_DENIED` | Set the named policy value to DWORD 0 under the Terminal Server key; see `references/install.md`. |
| `E_CHILD_SESSIONS_DECLINED` | The UAC consent was dismissed. Retry and accept it. |
| `E_ELEVATION_UNAVAILABLE` | No administrator session is available; an administrator must enable child sessions once. |
| `E_MANUAL_INTERVENTION` | A leftover logged-on child session or missing auth tier; follow `references/usage.md`. |
| `E_COORD_OUT_OF_RANGE` | Re-read `screen` from `status`; the coordinate is outside the child desktop. |
| `E_AGENT_TIMEOUT` / `E_AGENT_DEAD` | Close the host through its window and start a new session. |
| `E_DISCONNECTED` | The session is gone; restart the host. |

## References

- `references/install.md` — download, verify, install, register MCP, smoke test, upgrade, uninstall.
- `references/build-from-source.md` — SDK, build, contract tests, local publish, CI expectations.
- `references/usage.md` — full CLI/MCP reference, session states, error codes, operating loop.
