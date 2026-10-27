# Build from source

Use this path when there is no suitable release, when testing a change, or when packaging a release locally.

## 1. Prerequisites

- Windows 11 x64.
- .NET SDK selected by `global.json` (currently `10.0.402` or a newer `10.0.4xx` feature band).
- PowerShell 7 (`pwsh`) for the packaging script. Building and testing only need `dotnet`.

Confirm the SDK:

```powershell
dotnet --version
```

## 2. Build and run the contract tests

From the repository root:

```powershell
dotnet restore SoulComputerUse.slnx
dotnet build SoulComputerUse.slnx -c Release --no-restore
dotnet test tests\SoulComputerUse.ContractTests\SoulComputerUse.ContractTests.csproj `
  -c Release --no-build --no-restore
```

The contract suite is pure decisions, string checks, and source scans. It is safe in CI and requires no interactive desktop.

Do **not** run the integration suite on a shared or CI machine:

```text
tests\SoulComputerUse.IntegrationTests
```

It takes the production machine-wide lock, reads real Terminal Services policy, and refuses to start on a "dirty" machine with a leftover child session or helper process. Run it only on a clean, interactive Windows 11 test machine, after reading the repository's `docs/testing-conventions.md`.

## 3. Publish a runnable directory

The host, CLI, and MCP server must share one directory with the child-session agent and the elevation helper. Publish the host first, then the CLI and MCP into the same output:

```powershell
$out = Join-Path $PWD 'artifacts\local-publish'
dotnet publish src\SoulComputerUse.Host\SoulComputerUse.Host.csproj `
  -c Release -r win-x64 --self-contained true -o $out
dotnet publish src\SoulComputerUse.Cli\SoulComputerUse.Cli.csproj `
  -c Release -r win-x64 --self-contained true -o $out
dotnet publish src\SoulComputerUse.Mcp\SoulComputerUse.Mcp.csproj `
  -c Release -r win-x64 --self-contained true -o $out
```

Run it:

```powershell
& "$out\SoulComputerUse.Cli.exe" --help
Start-Process -FilePath "$out\SoulComputerUse.Host.exe" -ArgumentList '--new-session'
```

`SoulComputerUse.Cli.exe` and `SoulComputerUse.Mcp.exe` are separate entry points; the host does not launch them. The host does launch the agent and helper from its own directory.

## 4. Package the release locally

```powershell
pwsh -NoProfile -File .\scripts\package-release.ps1 `
  -Version 0.1.0-local `
  -OutputDirectory artifacts\release
```

The script publishes the host, CLI, and MCP into a staging directory, verifies the five required executables, copies `LICENSE`, creates:

- `SoulComputerUse-win-x64.zip`
- `soul-computer-use-skill.zip`
- `SHA256SUMS.txt`

It prints the absolute paths as JSON.

## 5. What CI does

`.github/workflows/ci.yml` restores, builds Release, runs the contract suite, packages a `0.0.0-ci.<run>` archive on non-PR runs, and verifies that the Vercel skills CLI discovers `soul-computer-use`.

`.github/workflows/release.yml` repeats the build and contract tests on a `v*` tag, packages the same assets, and publishes a GitHub Release with generated notes. See the repository's `docs/release.md`.

## 6. Common failures

- `NU1301` or a restore timeout: NuGet is unreachable. `NuGet.config` clears all sources except `nuget.org`; fix the network or restore from a local cache.
- `NETSDK1045`: the installed SDK is older than `global.json` requires. Install a `10.0.4xx` SDK.
- `MSB3021` while publishing into a shared directory: a previous host process still has a file open. Close it and retry.
- `SoulComputerUse.SessionAgent.exe is missing`: publish the host and the CLI/MCP into the same directory; do not copy only the host output.
