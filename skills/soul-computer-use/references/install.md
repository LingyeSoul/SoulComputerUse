# Manual installation

This is the human path: download a release, verify it, unpack it, and register the MCP server. Use `../scripts/install.ps1` for the automated version of the same steps.

## 1. Check the machine

- Windows 11 x64.
- One Windows account: the child session inherits the signed-in account.
- Administrator consent once, when the host offers to enable child sessions.
- `fDenyTSConnections` and `fDenyChildConnections` must both be DWORD `0` under:

  ```text
  HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server
  ```

  The host refuses to connect when either value is missing or non-zero, because guessing "allowed" can log the operator out of the console desktop. On a machine you own, an elevated shell can set them:

  ```powershell
  reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f
  reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyChildConnections /t REG_DWORD /d 0 /f
  ```

  Do not enable `WTSEnableChildSessions` by hand: the product turns it on through its UAC helper and restores it when the session ends.

## 2. Download

Pick the newest published build:

| Asset | URL |
|---|---|
| Release page | `https://github.com/LingyeSoul/SoulComputerUse/releases/latest` |
| Application archive (stable asset name) | `https://github.com/LingyeSoul/SoulComputerUse/releases/latest/download/SoulComputerUse-win-x64.zip` |
| Skill archive (stable asset name) | `https://github.com/LingyeSoul/SoulComputerUse/releases/latest/download/soul-computer-use-skill.zip` |
| Newest `main` build | the `SoulComputerUse-win-x64-ci` artifact on a CI workflow run |

From the release page, take these two assets:

- `SoulComputerUse-win-x64.zip`
- `SHA256SUMS.txt`

With the GitHub CLI:

```powershell
gh release download --repo LingyeSoul/SoulComputerUse `
  --pattern "SoulComputerUse-win-x64.zip" `
  --pattern "SHA256SUMS.txt" `
  --dir "$env:USERPROFILE\Downloads\SoulComputerUse"
```

The newest build from `main` is also available as the `SoulComputerUse-win-x64-ci` artifact on the CI workflow run.

The archive is self-contained: it includes the .NET runtime, so no separate .NET installation is required.

## 3. Verify

The archive is unsigned, so verification is the checksum, not a publisher prompt:

```powershell
$download = Join-Path $env:USERPROFILE 'Downloads\SoulComputerUse'
$zip = Join-Path $download 'SoulComputerUse-win-x64.zip'
$line = Get-Content -LiteralPath (Join-Path $download 'SHA256SUMS.txt') |
  Where-Object { $_ -match '^([0-9a-fA-F]{64})\s+SoulComputerUse-win-x64\.zip$' } |
  Select-Object -First 1
if (-not $line) { throw "SHA256SUMS.txt has no entry for SoulComputerUse-win-x64.zip" }
$expected = [regex]::Match($line, '^([0-9a-fA-F]{64})').Groups[1].Value.ToLowerInvariant()
$actual = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actual -ne $expected) { throw "SHA-256 mismatch: expected $expected, got $actual" }
```

Stop on a mismatch. Do not run the archive.

## 4. Install

Install to a stable per-user directory:

```powershell
$install = Join-Path $env:LOCALAPPDATA 'SoulComputerUse\app'
if (Test-Path -LiteralPath $install) {
  if (Get-ChildItem -LiteralPath $install -Force) {
    throw "Remove the existing installation first: $install"
  }
} else {
  New-Item -ItemType Directory -Path $install | Out-Null
}
Expand-Archive -LiteralPath $zip -DestinationPath $install -Force
Get-ChildItem -LiteralPath $install -Recurse -File | Unblock-File
```

The directory must end up with all five executables together:

```text
SoulComputerUse.Host.exe
SoulComputerUse.SessionAgent.exe
SoulComputerUse.ChildSessionsHelper.exe
SoulComputerUse.Cli.exe
SoulComputerUse.Mcp.exe
```

The host derives the agent and helper paths from its own directory. Splitting the files across directories, or replacing one with a copy from another build, makes the session fail.

Optional: make `scu` available in new terminals by adding the install directory to the user `PATH` through **System Properties → Environment Variables**. Do not edit the machine-wide `PATH` just for one user.

## 5. Register the MCP server

Use the full path to the MCP executable as a local stdio command.

Claude Code:

```powershell
claude mcp add --scope user soul-computer-use -- "$install\SoulComputerUse.Mcp.exe"
```

Codex:

```powershell
codex mcp add soul-computer-use -- "$install\SoulComputerUse.Mcp.exe"
```

OpenCode (`opencode.json`):

```json
{
  "mcp": {
    "soul-computer-use": {
      "type": "local",
      "command": ["C:\\Users\\<you>\\AppData\\Local\\SoulComputerUse\\app\\SoulComputerUse.Mcp.exe"],
      "enabled": true
    }
  }
}
```

The MCP server is only a client: it does not own the session. Without one, call the MCP `start_session` tool (or run `scu start`) to open the product window; session tools answer `E_NOT_READY` until the host takes the lock.

## 6. Smoke test

```powershell
& "$install\SoulComputerUse.Cli.exe" --help
```

This must print the command table and exit with code `0`. It proves the self-contained runtime and the apphost both work.

Then start a session through the product:

```powershell
& "$install\SoulComputerUse.Cli.exe" start
```

`start` prints `ok` as soon as the host takes the machine lock; it does not wait for the login. The product window appears and the human completes the Windows login in that window. Until the login finishes, `status` answers `E_NOT_READY` with `retryable: true`, which is the expected wait. When the title bar settles, run:

```powershell
& "$install\SoulComputerUse.Cli.exe" status
& "$install\SoulComputerUse.Cli.exe" shot --out "$env:TEMP\soul-computer-use.png"
```

`status` must report `state=ready`, a `sessionId`, an `authTier`, a `gateStrength`, and `screen=<width>x<height>`. The screenshot must be the child desktop, not the console desktop.

## 7. Upgrade and uninstall

Upgrade:

1. Close the host through its window and confirm the end of the session.
2. Replace the contents of the install directory with the new release.
3. Run the `scu --help` smoke test again.

Uninstall:

1. Close the host through its window and confirm the end of the session.
2. Remove the install directory.
3. Remove the `soul-computer-use` MCP entry from the agent client.
4. Remove `%LOCALAPPDATA%\SoulComputerUse` if no session record should remain.

## Troubleshooting the install

- `SoulComputerUse.Host.exe` starts and immediately closes: run it from a terminal so the startup error reaches stderr, or check the product window dialog. The message names the missing file or policy value.
- SmartScreen or antivirus warns about an unsigned executable: the release is unsigned. Verify the SHA-256 and only then choose "Run anyway"; do not disable antivirus.
- `E_CHILD_SESSIONS_DECLINED`: the UAC prompt for `SoulComputerUse.ChildSessionsHelper.exe` was dismissed. Start again and accept it.
- `E_ELEVATION_UNAVAILABLE`: the machine has no administrator session available. An administrator must run the product once and accept the child-session prompt.
- `E_RDP_INBOUND_DENIED` or `E_CHILD_CONNECTIONS_DENIED`: one of the two policy values is missing or non-zero. Fix the named value and retry.
