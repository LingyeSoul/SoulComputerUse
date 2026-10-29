<p align="center">
  <img src="assets/brand/soul-computer-use.svg" width="120" alt="SoulComputerUse 图标" />
</p>

<h1 align="center">SoulComputerUse</h1>

<p align="center"><strong>给 agent 一个与你相对独立的 Windows 会话：登录和 UAC 仍由你本人完成。</strong></p>

<p align="center">
  <a href="https://github.com/LingyeSoul/SoulComputerUse/actions/workflows/ci.yml"><img src="https://github.com/LingyeSoul/SoulComputerUse/actions/workflows/ci.yml/badge.svg?branch=main" alt="CI" /></a>
  <a href="https://github.com/LingyeSoul/SoulComputerUse/releases/latest"><img src="https://img.shields.io/github/v/release/LingyeSoul/SoulComputerUse?style=flat-square&label=release" alt="Release" /></a>
  <img src="https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4?style=flat-square" alt="Windows 11 x64" />
  <img src="https://img.shields.io/badge/.NET-10-512BD4?style=flat-square" alt=".NET 10" />
  <a href="LICENSE"><img src="https://img.shields.io/badge/license-GPL--3.0-blue?style=flat-square" alt="GPL-3.0" /></a>
</p>

在 Windows 11 上用一个同账户子会话承载受控桌面，通过 `scu` CLI 或本地 MCP stdio server 提供截图、鼠标和键盘工具。Windows 登录界面由用户本人完成，程序与子会话代理全程不接收、不保存、不转发任何凭据。

## 为什么值得用

SoulComputerUse 不把 agent 放进你正在用的桌面，而是在同一个账户下再开一个 Windows 会话：它在另一个桌面上点击和输入，你的主桌面不被打断。隔离的是桌面与输入焦点，不是风险本身——子会话仍以你的身份运行。

| ✅ 它是 | ❌ 它不是 |
| --- | --- |
| 同一个 Windows 账户下的第二个会话，宿主窗口只是它的显示器，你的主桌面照常使用 | 另一个用户账户，也不是虚拟机或隔离容器，账户身份与权限始终是你自己的 |
| 与主桌面分开的桌面、输入焦点和窗口栈，两边的点击与快捷键互不干扰 | 文件、权限或注册表层面的安全沙箱，文件与权限仍属于同一个账户 |
| 只在本机命名管道上通信的本地工具，不监听任何网络端口 | 暴露在局域网或公网上的远程控制服务，没有可连接的监听端口 |
| 把 Windows 登录和 UAC 提示都留给你本人完成的产品 | 自动登录、凭据代输或记住密码的自动化，程序全程不碰凭据 |
| 同时提供 CLI 和 MCP stdio 两个入口，人和 agent 都能驱动 | 只绑定某一家 agent 框架的专用插件，换客户端不用换这套工具 |

## 能做什么

- **看**：`screenshot` 返回 PNG（≤ 12 MiB），坐标使用子会话的原生物理像素，不是窗口客户区坐标。
- **点**：移动、点击（左 / 右 / 中，1–3 次）、滚轮。
- **敲**：一次输入 ≤ 1000 字符的文本，或 1–5 个按键组合（如 `ctrl+s`、`ctrl+alt+delete`）；键入内容不写日志。
- **接给 agent**：本地 MCP stdio server，8 个工具，直接注册到 Claude Code、Codex、OpenCode 等客户端。
- **留给人**：`scu` 既能一条命令跑一次，也能进入交互式 `scu>` prompt。
- **装得轻**：Release 是 self-contained 的，不需要安装 .NET 运行时。

## 工作方式

```text
agent / 你
   │  MCP stdio 或 scu CLI
   ▼
scu / MCP 客户端
   │  本机命名管道（显式 DACL + 对端 SID 校验 + 一次性 nonce）
   ▼
Host 产品窗口（显示子会话桌面；Windows 登录由本人完成）
   │
   ▼
子会话 agent ──▶ 截图 / 鼠标 / 键盘
```

单机同一时间只有一个会话 owner，`Host`、CLI、MCP 都是这个 owner 的客户端。MCP server 不会自己持有会话：没有会话时调用 `start_session` 打开产品窗口，登录仍然由本人完成。

## 快速开始

前提：Windows 11 x64、同一个 Windows 账户；首次建立子会话需要一次管理员同意（`SoulComputerUse.ChildSessionsHelper.exe` 的 UAC 提示）。下面用 GitHub CLI 下载并解压，也可以从 [Releases](https://github.com/LingyeSoul/SoulComputerUse/releases/latest) 手动下载。

```powershell
# 1) 下载并解压到固定目录
gh release download --repo LingyeSoul/SoulComputerUse `
  --pattern "SoulComputerUse-win-x64.zip" `
  --pattern "SHA256SUMS.txt" `
  --dir "$env:USERPROFILE\Downloads\SoulComputerUse"
$install = Join-Path $env:LOCALAPPDATA 'SoulComputerUse\app'
Expand-Archive -LiteralPath "$env:USERPROFILE\Downloads\SoulComputerUse\SoulComputerUse-win-x64.zip" `
  -DestinationPath $install -Force

# 2) 打开产品窗口：普通权限运行，拿到单实例锁就返回，不等登录
& "$install\SoulComputerUse.Cli.exe" start

# 3) 在窗口里由你本人完成 Windows 登录，然后
& "$install\SoulComputerUse.Cli.exe" status
& "$install\SoulComputerUse.Cli.exe" shot --out "$env:TEMP\soul-computer-use.png"
```

登录等待期间 `status` 返回 `E_NOT_READY`（可重试），属正常状态。`start` 需以普通权限运行，调用方已提权时会被拒绝；等价地也可以手动启动宿主窗口：

```powershell
Start-Process -FilePath "$install\SoulComputerUse.Host.exe" -ArgumentList '--new-session'
```

把 MCP server 注册给 agent（`%LOCALAPPDATA%\SoulComputerUse\app\SoulComputerUse.Mcp.exe`）：

```powershell
claude mcp add --scope user soul-computer-use -- "$install\SoulComputerUse.Mcp.exe"
codex mcp add soul-computer-use -- "$install\SoulComputerUse.Mcp.exe"
```

> [!IMPORTANT]
> Release 产物未做代码签名。安装前请按 `SHA256SUMS.txt` 校验 SHA-256，不要为了绕过 SmartScreen 而关闭杀毒软件；完整手动步骤见 [`skills/soul-computer-use/references/install.md`](skills/soul-computer-use/references/install.md)。

## 其他安装方式

<details>
<summary><strong>让 agent 安装</strong></summary>

先安装 skill：

```powershell
npx skills add LingyeSoul/SoulComputerUse --skill soul-computer-use -g -y
```

也可以直接从 Release 的 skill 压缩包安装：

```powershell
npx skills add https://github.com/LingyeSoul/SoulComputerUse/releases/latest/download/soul-computer-use-skill.zip -g -y
```

然后对 agent 说：

```text
帮我安装并注册 SoulComputerUse
```

skill 会调用 `skills/soul-computer-use/scripts/install.ps1`，从 Release 下载、校验、解压，并打印安装路径。安装 skill 本身不会安装程序。

</details>

<details>
<summary><strong>从源码构建</strong></summary>

```powershell
dotnet restore SoulComputerUse.slnx
dotnet build SoulComputerUse.slnx -c Release --no-restore
dotnet test tests\SoulComputerUse.ContractTests\SoulComputerUse.ContractTests.csproj `
  -c Release --no-build --no-restore
pwsh -NoProfile -File .\scripts\package-release.ps1 `
  -Version 0.1.0-local `
  -OutputDirectory artifacts\release
```

从源码构建需要 `global.json` 指定的 .NET SDK。详细说明见 [`skills/soul-computer-use/references/build-from-source.md`](skills/soul-computer-use/references/build-from-source.md)。

</details>

## 命令速查

`scu` 不带参数时进入交互式 prompt。

| 命令 | 作用 |
| --- | --- |
| `status` | 打印一行状态 |
| `shot [--out <path>]` | 有 `--out` 时写出 PNG 并打印路径，否则输出 base64 |
| `move <x> <y>` | 移动指针 |
| `click <x> <y> --button <left\|right\|middle> --count <n>` | 点击，两个选项都必填 |
| `scroll <x> <y> <dx> <dy>` | 在指定点滚动 |
| `type <text>` | 输入文本；prompt 中 `type ` 之后的内容原样保留 |
| `key <k1>+<k2>` | 按键组合，如 `ctrl+s` |
| `start` | 在新会话上打开产品窗口，拿到锁即打印 `ok` |
| `quit` | 退出 prompt，不关闭会话 |

MCP 工具：`get_status`、`screenshot`、`mouse_move`、`mouse_click`、`mouse_scroll`、`key_type`、`key_press`、`start_session`。参数与状态机细节见 [`skills/soul-computer-use/references/usage.md`](skills/soul-computer-use/references/usage.md)，权威契约见 [`docs/interface-contract.md`](docs/interface-contract.md)。

## 安全边界

- 程序没有密码输入框、自动登录、记住密码或凭据代输路径。
- 子会话代理只在子会话内启动；在控制台会话命中身份闸会立即退出。
- owner 管道带显式 DACL、对端 SID 校验和一次性 nonce，不监听网络。
- `blocked`（UAC 安全桌面）和 `awaiting-credentials`（等待系统登录）下不接受输入类工具。
- 子会话与宿主使用同一个 Windows 账户；账户不一致会在 IPC 握手中被拒绝。
- 子会话不是安全沙箱：隔离的是桌面与输入焦点，文件与权限仍属于同一个账户。
- 截图和键入内容属于敏感数据，不应写入日志、issue 或仓库。
- 仅 Windows 11 x64，仅本机命名管道，没有网络控制面。

## 环境要求

- Windows 11 x64，同一个 Windows 账户。
- 首次建立子会话需要一次管理员同意（`SoulComputerUse.ChildSessionsHelper.exe` 的 UAC 提示）。
- 两个策略值必须为 DWORD `0`，缺失按禁止处理：

  ```text
  HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\Terminal Server
    fDenyTSConnections
    fDenyChildConnections
  ```

- 下载的 Release 是 self-contained 的，不需要安装 .NET 运行时；从源码构建需要 `global.json` 指定的 .NET SDK。

## 更多文档

- CLI / MCP 工具表、状态机、坐标模型和错误码：[`skills/soul-computer-use/references/usage.md`](skills/soul-computer-use/references/usage.md)
- 接口契约（权威版本）：[`docs/interface-contract.md`](docs/interface-contract.md)
- 子会话代理运行说明：[`docs/agent-runtime.md`](docs/agent-runtime.md)

## CI 与发布

- `.github/workflows/ci.yml`：push/PR 上恢复、Release 构建、跑契约测试，并运行一组不依赖交互桌面的真实 IPC/安全集成测试；非 PR 运行会打包 `0.0.0-ci.<run>` 构建产物。
- `.github/workflows/release.yml`：推送 `v*` 标签（或在 Actions 里手动指定已存在的标签）时，重新构建、跑契约测试、生成 GitHub Release 并上传：
  - `SoulComputerUse-win-x64.zip`
  - `soul-computer-use-skill.zip`
  - `SHA256SUMS.txt`

发布流程与本地打包见 [`docs/release.md`](docs/release.md)。

## 许可证

GPL-3.0，见 [`LICENSE`](LICENSE)。

---

<p align="center">如果 SoulComputerUse 帮到了你，点个 ⭐ 就是最好的支持。</p>
