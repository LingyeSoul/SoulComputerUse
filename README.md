# SoulComputerUse

在 Windows 11 上用一个同账户子会话承载受控桌面，通过 `scu` CLI 或本地 MCP stdio server 提供截图、鼠标和键盘工具。Windows 登录界面由用户本人完成，程序与子会话代理全程不接收、不保存、不转发任何凭据。

## 能力边界

- 仅 Windows 11 x64，仅本机命名管道，没有网络控制面。
- 子会话与宿主使用同一个 Windows 账户；账户不一致会在 IPC 握手中被拒绝。
- 单机同一时间只有一个会话 owner；`Host`、CLI、MCP 都是这个 owner 的客户端。
- `blocked`（UAC 安全桌面）和 `awaiting-credentials`（等待系统登录）下不接受输入类工具。

## 前置条件

- Windows 11 x64，同一个 Windows 账户。
- 首次建立子会话需要一次管理员同意（`SoulComputerUse.ChildSessionsHelper.exe` 的 UAC 提示）。
- 两个策略值必须为 DWORD `0`，缺失按禁止处理：

  ```text
  HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server
    fDenyTSConnections
    fDenyChildConnections
  ```

- 下载的 Release 是 self-contained 的，不需要安装 .NET 运行时；从源码构建需要 `global.json` 指定的 .NET SDK。

## 获取与安装

### 方式一：下载 Release（推荐）

把下文的 `<owner>` 换成仓库实际所有者（例如 `your-org/SoulComputerUse`）。在仓库的 **Releases** 页面下载：

- `SoulComputerUse-win-x64.zip`
- `SHA256SUMS.txt`

```powershell
gh release download --repo <owner>/SoulComputerUse `
  --pattern "SoulComputerUse-win-x64.zip" `
  --pattern "SHA256SUMS.txt" `
  --dir "$env:USERPROFILE\Downloads\SoulComputerUse"
```

压缩包是 self-contained 的，不需要另外安装 .NET 运行时。完整的手动步骤（校验 SHA-256、解压、MCP 注册、升级、卸载、注册表策略值）见：

- [`skills/soul-computer-use/references/install.md`](skills/soul-computer-use/references/install.md)

### 方式二：让 agent 安装

先安装 skill（`<owner>` 换成仓库实际所有者）：

```powershell
npx skills add <owner>/SoulComputerUse --skill soul-computer-use -g -y
```

也可以直接从 Release 的 skill 压缩包安装：

```powershell
npx skills add https://github.com/<owner>/SoulComputerUse/releases/latest/download/soul-computer-use-skill.zip -g -y
```

然后对 agent 说：

```text
帮我安装并注册 SoulComputerUse
```

skill 会调用 `skills/soul-computer-use/scripts/install.ps1`，从 Release 下载、校验、解压，并打印安装路径。安装 skill 本身不会安装程序。

### 方式三：从源码构建

```powershell
dotnet restore SoulComputerUse.slnx
dotnet build SoulComputerUse.slnx -c Release --no-restore
dotnet test tests\SoulComputerUse.ContractTests\SoulComputerUse.ContractTests.csproj `
  -c Release --no-build --no-restore
pwsh -NoProfile -File .\scripts\package-release.ps1 `
  -Version 0.1.0-local `
  -OutputDirectory artifacts\release
```

详细说明见 [`skills/soul-computer-use/references/build-from-source.md`](skills/soul-computer-use/references/build-from-source.md)。

## 快速开始

```powershell
$install = Join-Path $env:LOCALAPPDATA 'SoulComputerUse\app'
& "$install\SoulComputerUse.Cli.exe" --help
& "$install\SoulComputerUse.Cli.exe" start
```

`start` 需以普通权限运行（调用方已提权时会被拒绝），在宿主拿到单实例锁后立即返回，不会等登录。等价地，也可以手动启动宿主窗口：

```powershell
Start-Process -FilePath "$install\SoulComputerUse.Host.exe" -ArgumentList '--new-session'
```

在产品窗口里由本人完成 Windows 登录；登录等待期间 `status` 返回 `E_NOT_READY`（可重试），属正常状态。然后：

```powershell
& "$install\SoulComputerUse.Cli.exe" status
& "$install\SoulComputerUse.Cli.exe" shot --out "$env:TEMP\soul-computer-use.png"
```

MCP 客户端注册的 stdio 命令是：

```text
%LOCALAPPDATA%\SoulComputerUse\app\SoulComputerUse.Mcp.exe
```

MCP server 只是 owner 的客户端，不会自己持有会话；没有会话时调用 `start_session` 工具（等价于 `scu start`）打开产品窗口，登录仍由本人完成。

## 使用说明

- CLI / MCP 工具表、状态机、坐标模型和错误码：[`skills/soul-computer-use/references/usage.md`](skills/soul-computer-use/references/usage.md)
- 接口契约（权威版本）：[`docs/interface-contract.md`](docs/interface-contract.md)
- 子会话代理运行说明：[`docs/agent-runtime.md`](docs/agent-runtime.md)

## CI 与发布

- `.github/workflows/ci.yml`：push/PR 上恢复、Release 构建、跑契约测试；非 PR 运行会打包 `0.0.0-ci.<run>` 构建产物。
- `.github/workflows/release.yml`：推送 `v*` 标签（或在 Actions 里手动指定已存在的标签）时，重新构建、跑契约测试、生成 GitHub Release 并上传：
  - `SoulComputerUse-win-x64.zip`
  - `soul-computer-use-skill.zip`
  - `SHA256SUMS.txt`

发布流程与本地打包见 [`docs/release.md`](docs/release.md)。

## 安全说明

- 程序没有密码输入框、自动登录、记住密码或凭据代输路径。
- 子会话代理只在子会话内启动；在控制台会话命中身份闸会立即退出。
- owner 管道带显式 DACL、对端 SID 校验和一次性 nonce，不监听网络。
- 截图和键入内容属于敏感数据，不应写入日志、issue 或仓库。
- Release 产物未做代码签名；请先校验 `SHA256SUMS.txt`，不要为了绕过 SmartScreen 而关闭杀毒软件。

## 许可证

GPL-3.0，见 [`LICENSE`](LICENSE)。
