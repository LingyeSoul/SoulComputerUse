# scu start 与 MCP start_session 设计（契约 v2.8 草案）

- 日期：2026-10-27
- 状态：待主人批准（批准前不写产品代码）
- 方案：B（启动器 + 启动中语义）
- 影响面：`docs/interface-contract.md` §2/§3/§5/§6、CLI、MCP adapter、Windows 宿主发现层、skill 与 README

## 1. 意图与为什么

让 agent 能在需要时请求「新建会话」：由产品自动拉起宿主窗口，主人在窗口里亲手完成 Windows 登录，随后 agent 继续用同一套 CLI/MCP 工具操作。登录永远是人的动作，凭据铁律不变。

## 2. 现状与约束（已核实的代码事实）

- 唯一 owner 由 GUI 宿主的 `--new-session` 承担；owner 管道在 `AfterLogonSegment.StartOwnerPipe` 里启动，也就是**登录之后**才存在。
- 锁在 before-connect 阶段获取，所有权记录先写 `starting`，登录后依次写 `connecting`、`ready`。
- 发现层只看两件事（`OwnerProbe`）：锁对象是否存在、owner 管道是否应答；`OwnerDiscovery.Decide` 只有三条决策。**登录等待期 = 锁在 + 管道无 → 现在报 `E_LOCK_HELD`，消息还把用户往「找进程 / 关会话」上引。**
- CLI 的 `Program.Main` 先解析命令，再解析会话；`--help`、`quit` 是本地命令，其余命令都要 owner。命令集在 `CliCommandKind`，是封闭枚举。
- MCP 的 `Program.Main` **在启动时**解析会话，失败就退出；`McpServer` 持有一个 `ISessionClient`；工具集在 `SessionToolCatalog`，封闭枚举。→ 只要没有 owner，MCP server 根本不活，`start_session` 无从被调用。
- `McpToolExecutor.ExecuteAsync` 对每个工具先读一次 status，再动作。
- 所有权记录由 `OwnerRecordStoreAdapter` 读取，存活判据（hostPid 存活 + 启动时间匹配）已经在该类型里实现；`SessionRecoveryPolicy` 是纯函数。
- 子会话登录界面由产品窗口里的 RDP ActiveX 控件渲染，无窗口进程无法承载登录；契约 §7.1 禁止任何凭据通路。
- 高权限枚举不可用，已定下的原则是「不把没找到说成没有」。

## 3. 目标 / 非目标 / 反目标

目标：

- G1 `scu start` 与 MCP `start_session`：无 owner 且锁空闲时，从自身安装目录拉起 `SoulComputerUse.Host.exe --new-session`，宿主拿到锁后返回 `ok`。
- G2 已有 owner（管道应答）时幂等返回 `ok`，不重复开窗。
- G3 登录等待期（锁在、管道无、记录显示活着的 host 处于 `starting`/`connecting`）报 `E_NOT_READY`（retryable），不再是 `E_LOCK_HELD`。
- G4 拉起的宿主脱离调用者的作业对象（若在 job 中），避免 agent 会话结束时宿主陪葬、留下已登录子会话；确实无法脱离时显式拒绝，而不是默默留下风险。
- G5 MCP server 在没有会话时也能存活并响应协议，`start_session` 可用，其余工具返回 `E_NOT_READY` 工具错误（`isError`），不是进程退出。

非目标：

- 不做 headless 会话后端（`UnavailableSessionClient` 保持原样）。
- 不做「登录等待期就能查到 `awaiting-credentials`」的管道重构（原方案 C）。
- 不接入 `awaiting-credentials` / `blocked` 的宿主抓帧。
- 不引入凭据通路、不自动登录、不缓存验证结果。
- 不新增长驻进程、计划任务、系统配置。

反目标（做到也不算成功）：

- 让 agent 能替主人完成登录，或把登录界面藏到后台。
- `start` 阻塞到登录完成（会把 MCP 调用拖死；登录上限 10 分钟）。
- 拉起一个会在 agent 退出时被杀掉、并留下已登录子会话的宿主。
- 在同一台机器上拉起第二个 owner。

## 4. 关键决策

### 4.1 为什么不能只改 CLI，MCP 必须允许「无会话运行」

`start_session` 在被调用时必然**还没有会话**。若 MCP 仍在启动时解析会话，它会在 agent 还没能调用工具之前退出。因此 MCP adapter 必须从「启动时绑定一个 client」改为「按调用惰性解析」：每次工具调用先探 owner → 连上 → 用完即弃；解析失败走工具错误通道。这样 MCP 进程的寿命与协议会话一致，而不是与某个 owner 一致。CLI 不需要这个改造：它是一次性进程，`start` 在解析会话之前本地处理（与 `--help`、`quit` 同一处理层级）。

### 4.2 为什么登录等待期改报 E_NOT_READY

契约 §2 里 `E_NOT_READY` 的定义就是「会话未就绪（starting/connecting）」且 retryable；登录等待期在记录里正好是 `starting`。判断依据必须来自所有权记录：hostPid 存活 + 启动时间匹配 + 状态属于 {starting, connecting}。记录只读——不接管、不删除、不改写；读不到或不可解析时退回现有 `E_LOCK_HELD`（fail-closed 到旧行为）。这样 agent 的轮询语义是「等待」，而不是「有人占着锁，去关掉它」。

### 4.3 为什么 start 不等待登录

登录是人工动作，上限 10 分钟；MCP 工具调用等不起。`start` 的职责边界是「窗口出现、宿主拿到锁」，拿锁发生在连接和 UAC 之前，秒级可达；之后的等待交给 `get_status` / `status` 轮询。

### 4.4 为什么宿主必须脱离调用者的作业对象

Windows 默认把子进程并入父进程的 job；`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` 会在 job 关闭（agent 退出）时杀掉里面所有进程。若宿主是 MCP 的子进程，agent 一退，宿主被杀，子会话留在已登录状态——这正是契约最怕的残留。因此：不在 job 中 → 普通 `Process.Start`；在 job 中 → `CreateProcessW(CREATE_BREAKAWAY_FROM_JOB)`；job 不允许脱离（`ERROR_ACCESS_DENIED`）→ 显式失败并给出可操作消息（让主人手动启动一次）。

### 4.5 标准流隔离：完全不继承句柄

宿主以不继承句柄的方式创建（`bInheritHandles=false`，新进程、新进程组），因此不会继承调用者的 stdout/stderr。原因有两条：

- 若宿主继承 MCP 的 stdout 句柄，MCP 退出后该管道对 agent 客户端不会及时到达 EOF；
- 若宿主与 `scu start` 的控制台共享，Ctrl+C 或关闭终端可能连带杀掉宿主，留下已登录子会话。

代价如实接受：`HostConsole.HasUsableErrorStream()` 在宿主里为 false，启动失败只弹产品窗口对话框，启动器只能拿到退出码。因此 `E_HOST_LAUNCH_FAILED` 的消息写成「宿主窗口应已显示原因，若没有窗口请从终端直接运行宿主」，不假装能复述细节。

### 4.6 提权调用者：拒绝

调用者令牌若已提权（高完整性），`start` 直接失败并报 `E_HOST_LAUNCH_FAILED`，理由是不让 Windows 跳过 UAC 提示：契约 §3.1.1 把「用户确认 UAC」当作子会话开关的授权动作，提权宿主会静默拿掉这一步。降权拉起（linked token）虽然可行，但引入一条安全敏感的令牌处理路径，本轮不做。消息固定为「当前进程已提权，请以普通权限运行 `scu start`」。

### 4.7 `start_session` 不进入 IPC 方法面

`start_session` 是 MCP 适配器本地的进程动作，不属于 owner/agent 管道的方法集。因此：

- `SessionTool` 与 `SessionToolNames` 保持 7 个不变；
- MCP 的工具表在 `SessionToolCatalog` 之外扩展一个本地条目（建议 `McpLocalTool` 或直接在 `McpServer.ToolsList` 追加），`tools/call` 分派时先查本地工具，再查会话工具；
- `SessionIpcRules` 与两条管道服务器不改，未知方法仍按契约报错。

### 4.8 REPL 不做会话重绑

`CliRunner` 在启动时持有一个固定 `ISessionClient`。本轮 `start` 在 REPL 里只做幂等（已有会话返回 `ok`），不重建客户端；会话断开后需要重新运行 `scu`。这条边界写进 `references/usage.md`，不在本轮扩成「每个命令重新解析会话」的改造。

### 4.9 不加错误对象字段，改为 agent 侧的调用顺序

`E_NOT_READY` 现在同时表示「没有 owner」和「启动中/等登录」。不加子码，也不加新的错误对象字段；改为在 agent 契约里固定顺序：**先调 `start` / `start_session`，它返回 `E_NOT_READY` 就等待重试，返回 `ok` 后再轮询 `status`**。为此同步修改：

- `OwnerSessionResolver` 的无 owner 消息改为「没有会话在运行，可调用 `start` 拉起宿主」；
- skill 与 `references/usage.md` 的 `E_NOT_READY` 说明改为上述顺序；
- 契约 §2 的 `E_NOT_READY` 行不新增字段，只在 §3 说明两种来源。

### 4.10 错误码取舍

- 新增 `E_HOST_LAUNCH_FAILED`（retryable=false，出现 state=starting）：覆盖安装目录缺 `SoulComputerUse.Host.exe`、进程创建失败、等待期内宿主退出、调用者不在交互控制台会话、job 不允许脱离。每种原因一条固定消息模板（含文件名或数值，不含路径、账户、机器名）。
- 登录等待期 → 复用 `E_NOT_READY`（retryable=true），改的是发现层判定，不是新码。
- 锁被第三方持有 → 维持 `E_LOCK_HELD`；不加深「外来句柄」判定（那个判定要特权，本轮不做）。

## 5. 设计

### 5.1 共享启动器（Windows 层）

新增 `src/SoulComputerUse.Windows/Hosting/Launch/`：

- `HostExecutablePath.cs`：`Resolve(applicationDirectory)`，从调用者自身目录推导 `SoulComputerUse.Host.exe`，仿 `AgentExecutablePath` 的硬规则（绝不走 PATH、绝不外部传路径、缺失显式失败）。
- `IHostProcessStarter` + `HostProcessStarter`：负责真正创建进程，按 §4.4 脱离 job、按 §4.5 不继承任何句柄、按 §4.6 拒绝提权调用者；返回进程句柄/pid，供等待期观察存活。
- `IConsoleSessionGuard` + `ConsoleSessionGuard`：`WtsSessionProbe.GetCurrentProcessSessionId()` 必须等于 `GetActiveConsoleSessionId()`，否则拒绝。
- `IElevationGuard` + `ElevationGuard`：调用者令牌若为高完整性则拒绝（§4.6）。
- `HostProcessLauncher`：组合以上端口与 `IOwnerProbe`，纯编排：

```text
probe(锁, 管道)：
  管道应答            -> AlreadyRunning（幂等 ok）
  锁在 + 记录显示启动中 -> E_NOT_READY（retryable）
  锁在               -> E_LOCK_HELD
  锁空闲             -> 继续
guard：非交互会话     -> E_HOST_LAUNCH_FAILED
guard：已提权         -> E_HOST_LAUNCH_FAILED
resolve：缺 Host.exe -> E_HOST_LAUNCH_FAILED
start：创建失败       -> E_HOST_LAUNCH_FAILED
wait（200ms 轮询，上限 30s）：
  锁出现             -> Started（ok）
  宿主退出           -> 重新探一次管道；应答则 AlreadyRunning，否则 E_HOST_LAUNCH_FAILED（带退出码）
  超时               -> E_HOST_LAUNCH_FAILED（提示查看产品窗口/对话框，不自动重拉第二个窗口）
```

返回 `HostStartOutcome { AlreadyRunning, Started }`；两种都对应 `ok`。

### 5.2 发现层：启动中观测

- `IOwnerProbe` 增加 `bool IsLiveOwnerStarting()`：读本账户所有权记录 + 进程存活判据，状态 ∈ {starting, connecting} 时为 true；记录缺失/损坏/不可读时为 false。**只读**。
- `OwnerDiscovery.Decide(lockExists, pipeReachable, liveOwnerStarting)` 增加一格：锁在、管道无、启动中 → 新决策 `RefuseStartInProgress`。
- `OwnerSessionResolver` 对该决策抛 `E_NOT_READY`，消息写明「会话正在启动，可能正在等待操作者登录；登录完成后重试」。
- 记录读取复用 `SessionOwnerStore` + `SessionRecoveryPolicy` 已有判据，不重复实现。

### 5.3 CLI

- `CliCommandKind.Start`；`CliCommandParser` 接受 `start`（无参数、无选项）；`CliHelp` 增行。
- `Program.Main` 在解析会话之前处理 `start`（与 `--help`、`quit` 同级），调用 `HostProcessLauncher`；成功打印 `ok`。
- REPL 中 `start` 由 `CliRunner` 处理（与 `quit`、`--help` 相同的现有模式），指向同一个启动器；已有会话时幂等。
- 其余命令行为不变。

### 5.4 MCP

- **本地工具**：`start_session` 是 MCP 适配器本地工具，不进 `SessionTool`。实现上新增一个适配器内的工具表（建议 `McpLocalTools`），`tools/list` 先列会话工具再列本地工具；`tools/call` 先查本地工具名，命中则走启动器，未命中再走 `SessionToolNames.FromWireName`。
- `McpToolExecutor` 改为持有惰性会话解析器：
  - `start_session` 由本地工具分派处理，不读 status，调用 `HostProcessLauncher`，成功返回 `{"ok":true}`；
  - 其余工具：`await using ResolvedSession session = await resolver.ResolveAsync(...)`，随后流程不变；
  - 解析失败：`SessionOperationException` → 标准 `isError` 错误对象，`state` 字段按 `SessionErrorJson` 规则省略或按码推导。
- `McpServer` 构造参数由 `ISessionClient` 改为解析器；`Program.Main` 不再在启动时解析会话，也不再因 `E_NOT_READY` 退出；它只构造解析器与启动器。
- `Program.Main` 仍保留「stdio 不可用就退出」的现有失败路径，其余不变。

### 5.5 契约修改清单（草案文本）

- 标题版本 v2.7 → v2.8，括注追加「agent 请求建会话（`scu start` / `start_session`：本地拉起宿主窗口，登录仍由操作者亲手完成；登录等待期按 `E_NOT_READY` 可重试）」。
- **§3 进程拓扑改写**：原文「CLI 与 MCP server 一律先作为 IPC 客户端连 owner；连不上且锁空闲时才由自己起会话并持锁」改为「CLI 与 MCP server 一律先作为 IPC 客户端连 owner；连不上时按 §3.0 处理——显式请求建会话则拉起 GUI 宿主并由宿主持锁，未请求则失败」。headless 自持锁路径保留为**非生产、默认关闭**的并列路线（`SOULCOMPUTERUSE_ALLOW_HEADLESS`，且本构建后端为空）。
- **§6 最后一段改写**：删去「仅当无 owner 且锁空闲时才自行起会话并持锁」，改为「`start` 与 `quit`、`--help` 同为本地命令：不要求现有会话，`start` 拉起宿主窗口后立即返回」。
- §2 表加一行：

  | `E_HOST_LAUNCH_FAILED` | 宿主进程未能从调用方环境中拉起（安装目录缺宿主可执行文件、调用方不在交互控制台会话、调用方已提权、进程创建失败、或宿主在取得锁之前退出） | false | starting |

- §3 加一段：

  > **启动中识别（只读）**：CLI/MCP 发现锁对象存在但 owner 管道不可达时，若本账户所有权记录显示持有者进程存活且启动时间匹配、状态属于 {`starting`, `connecting`}，按 `E_NOT_READY`（retryable）回答，消息说明「会话正在启动，可能正在等待操作者登录」；其余情况维持 `E_LOCK_HELD`（外来句柄判定仍受特权限制，不变）。所有权记录在此路径上只读，不接管、不删除、不改写。

- §5 加 5.8：

  > ### 5.8 `start_session`
  > ```json
  > { "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false, "required": [] } }
  > ```
  > 无会话且锁空闲时拉起产品窗口（`SoulComputerUse.Host.exe --new-session`），窗口显示 Windows 登录界面，登录仍由操作者本人完成；宿主取得锁后返回 `{ "ok": true }`。已有会话时幂等返回同样结果。等待登录不是本工具的工作：失败与未就绪按 §2 错误对象返回。**本工具是 MCP 适配器本地工具，不进入 owner/agent 管道的方法集。**

- §6 表加一行 `start`（无参数），并注明：`start` 与 `quit`、`--help` 一样是**本地命令**，不需要现有会话。

### 5.6 日志与脱敏

启动器只写既有白名单字段；错误消息允许固定文件名（`SoulComputerUse.Host.exe`）与数值（退出码、超时秒数），不得出现绝对路径、账户、SID、机器名。job 判定失败的消息只描述「当前环境禁止脱离作业对象」，不点名进程。

## 6. 测试

契约测试（纯逻辑）：

- `CliCommandParser`：`start` 解析、带参数/选项拒绝；`CliHelp` 含 `start`。
- `OwnerDiscovery.Decide` 新真值表（含 `liveOwnerStarting` 三态组合）。
- MCP 本地工具表：`start_session` 出现在 `tools/list`、schema 为空对象、与 `SessionTool` 互不污染（`SessionToolNames.FromWireName("start_session")` 仍抛错）。
- `McpToolExecutor`：`start_session` 不读 status、调用启动器、返回 `{"ok":true}`；无会话时其它工具返回 `isError` 且不再是进程退出。
- 契约文本守护：§2 新码、§5.8、§6 新命令在文档里存在（沿用现有源码/文档扫描模式）。

集成测试（真机/真实管道，不碰生产锁名）：

- 启动器在 fake probe/fake starter 下：锁出现 → Started；宿主退出 → `E_HOST_LAUNCH_FAILED`；超时 → `E_HOST_LAUNCH_FAILED`；非交互会话 → `E_HOST_LAUNCH_FAILED`；已提权 → `E_HOST_LAUNCH_FAILED`；缺 exe → `E_HOST_LAUNCH_FAILED`；已有管道 → AlreadyRunning。
- 启动中判定：构造真实所有权记录 + 真实存活进程（TestSupport），`IsLiveOwnerStarting` 为真时 `status` 返回 `E_NOT_READY`；进程停止后回到 `E_LOCK_HELD`。
- 句柄隔离：用 TestSupport 作为「宿主」，断言它不继承调用者的 stdout 句柄、且不随调用者所在 job 关闭而退出。
- MCP stdio 端到端：无 owner 时 server 存活；`tools/list` 含 `start_session`；`tools/call start_session` 走 fake starter；`tools/call get_status` 得 `E_NOT_READY`。

手工验收（主人）：

1. `scu start` → 窗口出现、标题栏进入等待验证；
2. 主人亲手登录 → `scu status` 变 `ready`；
3. 登录等待期间另开一个 `scu status` → `E_NOT_READY` 且消息正确；
4. 关掉 agent 客户端后宿主仍存活、窗口仍在；
5. 关闭会话 → 子会话注销、锁释放、记录删除。

## 7. 留给实现者的小决定

- `OwnerDiscoveryDecision` 新成员命名（建议 `RefuseStartInProgress`）。
- 不继承句柄的具体创建方式（`ProcessStartInfo` 的 `UseShellExecute=false` + 无重定向是否足够，或需要 `CreateProcessW`）；只要满足「不继承、新进程组、句柄不泄漏」即可。job 脱离如果与「完全不继承」冲突，以「不被调用者带走」为优先。
- 等待期轮询间隔的常量归处（建议随 `HostProcessLauncher`）。
- MCP 本地工具表的容器形态（`McpLocalTool` 枚举或字符串表）。
- 宿主退出码到 `E_HOST_LAUNCH_FAILED` 消息的映射细节（只带数值，不复述宿主内部原因）。
- `ConsoleSessionGuard` 在远程桌面会话里是否放行（只要求「当前进程会话 == 活动控制台会话」）。

## 8. 文档与 skill 更新

- `skills/soul-computer-use/SKILL.md` 第 3 步：改为「先 `scu start`（或 MCP `start_session`）→ 等主人登录 → 轮询 status」，写明登录等待期 `E_NOT_READY` 是正常可重试状态；并写明 agent 提权时会失败。
- `references/usage.md`：工具表加 `start_session`，错误表加 `E_HOST_LAUNCH_FAILED`，状态说明补「登录等待期」，并写明 REPL 不重绑。
- `references/install.md`：冒烟测试补 `scu start`。
- `README.md`：快速开始改为 `start` 优先。
- `docs/agent-runtime.md`：启动拓扑补「启动器」一节与不继承句柄的说明。
- `.github/workflows/*`、打包脚本：无需改动（宿主仍在同一个目录、名字不变）。

## 9. 残余风险

- 宿主以不继承句柄的方式拉起，启动失败只能由产品窗口的对话框说明原因，启动器的错误消息只能给退出码。这是 §4.5 明确接受的代价。
- 提权调用者被拒绝（§4.6）。若 agent 以管理员身份运行，`start` 会失败，需主人以普通权限手动启动或重开 agent。
- 登录等待期仍看不到 `awaiting-credentials`（管道不存在）；这是方案 B 的已知边界，`E_NOT_READY` 只是让语义诚实，不提供状态可见性。
- REPL 不重绑（§4.8）：会话断开后需重开 `scu`。
- 记录文件被同账户恶意进程伪造为 `starting` 会让客户端误等；影响是拒绝服务（可重试等待），不是绕过门禁；与现有记录伪造风险同级，不在本轮加防护。