# 发布流程

本文只描述维护者如何产出和发布构建，不重复安装步骤。安装见 `skills/soul-computer-use/references/install.md`。

## 版本规则

- 标签格式：`v<major>.<minor>.<patch>`，可选预发布后缀，例如 `v1.2.3`、`v1.2.3-rc.1`。
- 标签是唯一的版本来源；工作流从标签去掉前导 `v` 后传给 `-p:Version`，不维护单独的版本文件。
- 预发布标签（版本里带 `-`）会创建 GitHub prerelease。

## 自动发布

`.github/workflows/release.yml` 在以下时机运行：

1. 推送 `v*` 标签；
2. 在 Actions 页面手动触发，并填写一个**已存在**的标签。

工作流执行顺序：

1. 按标签检出源码；
2. 安装 `global.json` 指定的 .NET SDK；
3. 校验标签格式并推导版本；
4. `dotnet restore` + Release 构建；
5. 跑契约测试；失败即停止，不发布；
6. 调用 `scripts/package-release.ps1` 打包；
7. 创建或复用 GitHub Release，上传三个资产；
8. 预发布标签自动标记为 prerelease。

权限只使用工作流自带的 `GITHUB_TOKEN`（`contents: write`），不需要个人访问令牌。

## 产物

| 文件 | 用途 |
|---|---|
| `SoulComputerUse-win-x64.zip` | self-contained 应用；解压后直接运行，不需要 .NET 运行时。版本由 Release 标签标识，资产名保持稳定，便于 `releases/latest/download` 直链 |
| `soul-computer-use-skill.zip` | `npx skills add https://github.com/LingyeSoul/SoulComputerUse/releases/latest/download/soul-computer-use-skill.zip` 可直接安装的 skill 包，根目录含 `SKILL.md` |
| `SHA256SUMS.txt` | 两个压缩包的 SHA-256，安装脚本会强制校验 |

应用压缩包内必须同时存在：

```text
SoulComputerUse.Host.exe
SoulComputerUse.SessionAgent.exe
SoulComputerUse.ChildSessionsHelper.exe
SoulComputerUse.Cli.exe
SoulComputerUse.Mcp.exe
LICENSE
install-manifest.json
```

`Host` 从自身目录推导代理和提权 helper 的路径，拆开文件会直接启动失败。

## 完整性清单

应用压缩包根目录包含 `install-manifest.json`，记录压缩包内**全部文件**（含 .NET 运行时 DLL 与子目录文件）各自的 SHA-256。键为相对压缩包根目录、以 `/` 分隔的相对路径（清单自身除外）：

```json
{
  "manifestVersion": 2,
  "productVersion": "1.2.3",
  "algorithm": "SHA-256",
  "files": {
    "SoulComputerUse.ChildSessionsHelper.exe": "<sha256>",
    "SoulComputerUse.Cli.exe": "<sha256>",
    "SoulComputerUse.Host.exe": "<sha256>",
    "SoulComputerUse.Mcp.exe": "<sha256>",
    "SoulComputerUse.SessionAgent.exe": "<sha256>",
    "coreclr.dll": "<sha256>",
    "runtimes/win-x64/some-native.dll": "<sha256>"
  }
}
```

（条目为示意，实际清单覆盖压缩包内每一个文件。）

安装脚本对解压出的 payload 做双向校验：manifest 列出的每个文件必须存在且哈希一致，盘上的实际文件也必须与 manifest 条目完全相等——多出的未列名文件同样拒绝（fail-closed），不给"解压后、提权复制前"的窗口期留替换空间。键必须是不含 `..`、不含盘符/绝对路径、不含反斜杠、以 `/` 分隔的安全相对路径。`manifestVersion` 不是 2、算法不是 SHA-256、缺少必需 exe 条目、哈希不一致，一律拒绝安装。复制到 `%ProgramFiles%\SoulComputerUse` 后按同一规则再校验一次。

`manifestVersion` 2 的语义：v1 只按根目录文件名列出五个 exe；v2 改为相对路径键并覆盖全部文件。读取端只认自己理解的版本，解析到其他版本直接拒绝，不猜测旧格式。

它不是签名：清单只能证明文件与清单一致，不能证明是谁构建了发布。只有 Authenticode 签名才能证明构建者身份。

## 本地打包

```powershell
pwsh -NoProfile -File .\scripts\package-release.ps1 `
  -Version 1.2.3 `
  -OutputDirectory artifacts\release
```

脚本会：

1. 依次 publish `Host`、`Cli`、`Mcp` 到同一个临时目录；
2. 复制 `LICENSE`；
3. 写入 `install-manifest.json`（staging 目录内全部文件的 SHA-256，键为 `/` 分隔的相对路径），并核对五个必需 exe 都在清单里；
4. 生成应用 zip 和 skill zip；
5. 写入 `SHA256SUMS.txt`；
6. 输出三个产物的路径（JSON）。

## 发布前检查

- `dotnet build SoulComputerUse.slnx -c Release` 无警告失败（仓库把警告当错误）。
- 契约测试全绿；集成测试只在干净的真机跑，不放进 CI。
- 应用 zip 解开后，五个 exe 在同一层目录。
- 在目标机器上执行 `SoulComputerUse.Cli.exe --help`，确认 self-contained 运行时可用。
- 在目标机器上跑 `Host.exe --new-session`，确认用户能亲手完成系统登录，且 `scu status` 最终为 `ready`。
- `SHA256SUMS.txt` 里的哈希与两个 zip 一致。

## 回滚

发布后发现问题时：

1. 不要删除旧标签或覆盖旧资产；`gh release upload --clobber` 只用于重跑同一次发布。
2. 修复后打新的补丁标签，例如 `v1.2.4`。
3. 如果必须下架某个版本，在 Release 页面标记为 prerelease 或删除该 Release，但保留标签，避免已下载的校验文件失去对应关系。

## 未做代码签名

Release 产物未做 Authenticode 签名，SmartScreen 可能提示未知发布者。文档要求用户先校验 SHA-256，再决定是否运行；不要通过关闭杀毒软件或系统防护来绕过提示。
