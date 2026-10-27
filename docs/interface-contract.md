# SoulComputerUse 接口契约（v2.8 · 定稿：单实例锁句柄模型、全机唯一锁、确认即注销、同账户 child session 地基（helper exe 经 UAC 开启/关回）、临时 HKCU Run 启动项拉起代理（v2.7：RDP alternate shell 在 child session 下经 2026-10-27 实测被忽略，机制改定，见 §3.1）、门禁三档——`hello-enrolled` Hello 强验证 / `system-credential` **子会话系统登录验证**（2026-10-26 用户实测确认：全新子会话先出 Windows 登录界面、密码由用户亲手输入；连接前必须确认无遗留已登录子会话）/ `none` 拒启；agent 请求建会话（`scu start` / `start_session`：本地拉起宿主窗口，登录仍由操作者亲手完成；登录等待期按 `E_NOT_READY` 可重试））

> 范围：会话状态机、错误码、单实例锁、代理启动与自退、IPC、MCP 工具、CLI 命令、凭据流程、关闭语义、坐标模型、脱敏与日志白名单、非目标。
> 本文全部使用虚构占位值（`example-session`、`192.0.2.10`、`/path/to/project`、`REDACTED`），不含真实路径、账户与机器信息。
> 只定契约不写实现；类型签名用伪代码，实现语言与命名以仓库现状为准。

## 1. 会话状态机

### 1.1 状态集（与标题栏七态一一对应，枚举名固定）

| 状态 | 枚举值 | 进入条件 | 离开去向 |
|---|---|---|---|
| 启动中 | `starting` | 进程拉起、锁获取成功后 | `connecting` / `disconnected` |
| 等待授权 | `awaiting-credentials` | 连接子会话后窗口显示 **Windows 登录界面**，等待用户亲自登录（子会话系统登录验证）；`hello-enrolled` 档开工时的 Hello 验证也进本状态 | `connecting`（登录完成）；关闭/放弃 → 终止（见 §7、§8） |
| 连接中 | `connecting` | 凭据通过，代理握手进行中 | `ready` / `disconnected` / `blocked` |
| 已就绪 | `ready` | 握手完成，可接受工具调用 | `blocked` / `disconnected` / `closing` |
| 断线 | `disconnected` | 管道/会话中断且不自动重连 | 终态（需人工重启会话） |
| 不支持 | `blocked` | 检测到 UAC 安全桌面 / 非交互会话 | `ready`（人工处理完成后恢复） |
| 关闭中 | `closing` | 用户确认「结束会话」 | 终态（进程退出） |

### 1.2 状态转移表（非法转移一律拒绝，见错误码 `E_STATE`）

| 从 \ 到 | starting | awaiting-credentials | connecting | ready | disconnected | blocked | closing |
|---|---|---|---|---|---|---|---|
| starting | — | ✓ | ✓ | ✗ | ✓ | ✗ | ✓ |
| awaiting-credentials | ✗ | — | ✓ | ✗ | ✓ | ✗ | ✓ |
| connecting | ✗ | ✗ | — | ✓ | ✓ | ✓ | ✓ |
| ready | ✗ | ✗ | ✗ | — | ✓ | ✓ | ✓ |
| disconnected | ✗ | ✗ | ✗ | ✗ | — | ✗ | ✓（清理路径） |
| blocked | ✗ | ✗ | ✗ | ✓ | ✓ | — | ✓ |
| closing | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | — |

### 1.3 各状态对工具调用的拒绝条件

- `ready`：全部工具可用。
- `blocked`：全部动作类工具拒绝（`E_BLOCKED`）；状态查询、截图类只读工具可用（截图用于人工判断 UAC）。
- `awaiting-credentials`：除 `get_status`、`screenshot` 外全部快速拒绝（`E_AWAITING_CREDENTIALS`），消息固定为「等待用户完成系统登录/验证」。
- `starting` / `connecting` / `closing`：全部拒绝（`E_NOT_READY` / `E_CLOSING`）。
- `disconnected`：全部拒绝（`E_DISCONNECTED`），不自动重连。
- **agent 无任何代输凭据路径**：不存在接受密码/ PIN 的字段、参数或消息类型。

## 2. 错误码表

错误对象统一结构（脱敏后）：

```json
{ "code": "E_LOCK_HELD", "message": "another SoulComputerUse session owns the single-instance lock", "retryable": false, "state": "starting" }
```

| code | 含义 | retryable | 出现 state |
|---|---|---|---|
| `E_LOCK_HELD` | 单实例锁已被其他进程持有；消息说明「当前有进程持锁」及查看持锁进程的方法 | false | starting |
| `E_LOCK_STALE_RECORD` | 残留所有权记录按判据不可安全接管，提示人工处理（原名 `E_LOCK_ABANDONED` 已随语义更名） | false | starting |
| `E_LOCK_FOREIGN_HANDLE` | 锁对象存在但无任何活着的会话进程持有（外来句柄）；消息如实声明普通账户无法枚举持有者并给出路（重启/退出可疑应用），不把「没找到」说成「没有」；不自动解绑、不杀别人进程 | false | starting |
| `E_AUTH_REQUIRED` | 门禁验证被用户拒绝或验证失败，会话不建立 | false | starting |
| `E_MANUAL_INTERVENTION` | 设备无任何可用系统验证方式（档位 `none`），需用户在系统层面配置后重试 | false | starting |
| `E_CHILD_SESSIONS_DISABLED` | UAC 提权失败或 `WTSEnableChildSessions` 开启 API 调用失败（未开启本身不是错误，走 UAC 正常路径） | false | starting |
| `E_CHILD_SESSIONS_DECLINED` | 用户拒绝了开启子会话开关的 UAC 授权（`ERROR_CANCELLED`） | false | starting |
| `E_ELEVATION_UNAVAILABLE` | 机器无可用管理员会话/提权机会（`ShellExecuteEx` 失败且非 `ERROR_CANCELLED`），需管理员先开启一次 | false | starting |
| `E_CHILD_CONNECTIONS_DENIED` | 子会话允许连接被禁或值缺失/类型异常（`fDenyChildConnections` 非 0、缺失、非 DWORD），拒绝连接；消息点名值名与解法 | false | starting |
| `E_RDP_INBOUND_DENIED` | RDP 入站被禁或值缺失/类型异常（`fDenyTSConnections` 非 0、缺失、非 DWORD），拒绝连接以防接管主桌面会话；消息点名值名与解法 | false | starting |
| `E_STATE` | 当前状态不接受该操作（非法转移） | false | 任意 |
| `E_NOT_READY` | 会话未就绪（starting/connecting） | true | starting/connecting |
| `E_AWAITING_CREDENTIALS` | 等待用户完成系统验证（当前 `authTier` 档位），agent 不可代输 | false | awaiting-credentials |
| `E_DISCONNECTED` | 会话已断线，需人工重启 | false | disconnected |
| `E_BLOCKED` | 安全桌面/非交互会话阻止操作 | false | blocked |
| `E_CLOSING` | 会话正在关闭 | false | closing |
| `E_AGENT_TIMEOUT` | 子会话代理心跳超时 | true | 任意运行态 |
| `E_AGENT_DEAD` | 子会话代理进程退出 | false | 任意运行态 |
| `E_AGENT_WRONG_SESSION` | 代理命中自退闸（身份闸/认领闸）：落在主桌面会话或认领信息不符（**仅代理自退日志用，不进工具错误通道**） | false | 代理侧自退 |
| `E_AGENT_UNCLAIMED` | 代理 30 秒内未被宿主认领，自退（**仅代理自退日志用，不进工具错误通道**） | false | 代理侧自退 |
| `E_IPC_PROTOCOL` | 管道消息格式/版本不符 | false | 任意 |
| `E_IPC_PEER` | 管道对端 PID/SID 校验失败或 hello nonce 不匹配 | false | 任意 |
| `E_INPUT_INVALID` | 输入参数校验失败（message 含字段名与原因，不含原始值） | false | 任意 |
| `E_COORD_OUT_OF_RANGE` | 坐标超出子会话屏幕边界（message 含边界值） | false | ready/blocked |
| `E_SCREENSHOT_TOO_LARGE` | 截图编码后超过大小上限 | false | ready/blocked |
| `E_AGENT_ERROR` | 子会话执行动作失败（透传脱敏原因） | true | ready |
| `E_HOST_LAUNCH_FAILED` | 宿主进程未能从调用方环境中拉起（安装目录缺宿主可执行文件、调用方不在交互控制台会话、调用方已提权、进程创建失败、或宿主在取得锁之前退出） | false | starting |

`message` 规则：只含固定模板 + 参数化的数值/枚举（坐标、边界、状态名、原因码），不得含 §10 黑名单字段。`E_AGENT_ERROR` 的原因**只允许枚举原因码**（如 `uac-shield`、`window-gone`、`input-rejected`），代理不得发送自由文本原因。

## 3. 单实例锁（全机唯一）

- **进程拓扑**：唯一 **owner 进程**持有锁——生产路径为 GUI `Host`；CLI/MCP 在无 owner 时自行拉起 **headless 会话进程**并持锁的旧路径保留为**非生产、默认关闭**的并列路线（`SOULCOMPUTERUSE_ALLOW_HEADLESS`，且本构建后端为空）。CLI 与 MCP server 一律先作为 **IPC 客户端**连 owner；连不上时按下段处理，锁被占则按客户端身份排队失败退出（`E_LOCK_HELD`）。
- **连不上 owner 时**：显式请求建会话——CLI `start` / MCP `start_session`——则从自身安装目录拉起 GUI 宿主 `SoulComputerUse.Host.exe --new-session`，由宿主取得并持有单实例锁；未请求建会话的调用不自行启动会话，显式失败（无 owner 且锁空闲时按 `E_NOT_READY`，retryable，消息指引先调用 `start` / `start_session`）。
- **启动中识别（只读）**：CLI/MCP 发现锁对象存在但 owner 管道不可达时，若本账户所有权记录显示持有者进程存活且启动时间匹配、状态属于 {`starting`, `connecting`}，按 `E_NOT_READY`（retryable）回答，消息说明「会话正在启动，可能正在等待操作者登录」；其余情况维持 `E_LOCK_HELD`（外来句柄判定仍受特权限制，不变）。所有权记录在此路径上只读，不接管、不删除、不改写。
- **对象**：命名 mutex，名字为固定 GUID 字符串 `Global\SoulComputerUse-{4C1F0E2A-9B7D-4E8A-A6C3-5D9E0F1A2B34}`（本 GUID 即契约钉死值，实现原样引用为代码常量，不再生成、不再变更；`Global\` 前缀保证跨 Windows 用户、跨 CLI/MCP 全机唯一；无权限创建时显式失败，不降级不重试）。
- **占用模型（v2.3 终版：句柄存在即占用）**：`CreateMutexW(initiallyOwned: false)` 建/开命名对象、**不要所有权**；占用判定 = `ERROR_ALREADY_EXISTS`；释放 = `CloseHandle`（任意线程、幂等），owner 句柄持有到进程退出。选它的理由：这是 Windows 官方单实例标准做法；无所有权即无线程亲缘，关闭序列不会被 CLR 异常从中间打断；abandoned 在生产形状下是死分支（owner 死则句柄关、对象销毁），不为它引入后台线程与握手复杂度。第三方句柄吊对象的风险评估为**非阻塞**：名字是本产品独有 GUID，句柄随进程退出自动关闭，误判「被占」是短暂现象、重试即可，不设解绑命令，但 `E_LOCK_HELD` 的错误消息要说明「当前有进程持锁」及查看持锁进程的方法。
- **等待语义**：**零等待**——`CreateMutexW` 返回 `ERROR_ALREADY_EXISTS` 即被占，立刻失败退出，不等待、不排队、不抢。
- **探测不创建（硬规则）**：任何「先探活再决定」的路径（MCP/CLI 先当客户端连 owner）必须用 `OpenMutexW`（只要 `SYNCHRONIZE`）**只探测、不创建**，成功路径立即 `CloseHandle`；只有真正决定当 owner 的一方才 `CreateMutexW` 并持有句柄到退出。
- **外来句柄诊断（仅诊断，不做自动解绑）**：若锁对象存在但**没有任何活着的本产品会话进程持有**（残留判据显示无活 owner 却 `ERROR_ALREADY_EXISTS`），报 `E_LOCK_FOREIGN_HANDLE`。诊断消息**如实声明能力边界**：普通账户无权枚举全机句柄，当前版本不提供持有者枚举（`CanIdentifyHolder = false`），直说「无法确定持有者」，并给可操作出路（重启机器 / 退出可疑应用）；**绝不把「没找到」说成「没有」**，也**绝不用「残留记录」的措辞**——残留记录可从记录侧清理，外来句柄不能，两种情形下用户按错误指引操作将无法解决问题。若日后要真能指出持有者，那是**带特权的诊断路径**，须单独提出并经用户确认后才实施，产品绝不自行加特权。
- **残留接管与判据 1（记录驱动，与锁模型解耦）**：恢复只看所有权记录 + 进程存活校验，每次成功拿锁后都跑（见下）；判据 1 = **成功创建且无 `ERROR_ALREADY_EXISTS`**——`AbandonedMutexException` 在本模型下不存在独立分支，不设 `AcquiredAfterAbandon` 之类结果。
- **所有权记录**（获取成功后写入每用户本地数据目录，按用户 SID 分文件：`%LOCALAPPDATA%\SoulComputerUse\owner-{SID}.json`；全机唯一性由 mutex 保证，记录仅用于诊断与接管判据；字段白名单如下，**不含**密码、用户名、窗口标题、输入正文、截图）：
- **写记录失败语义**：文件不可写或字段缺失时**显式失败**——持有锁的进程立即关闭句柄释放锁并以非 0 退出码退出，绝不「拿到锁但记不下所有权」地继续跑。
- **字段校验口径（v2.1 统一）**：读回记录时七字段全部必填，`hostPid ≤ 0`、时间为 default、sessionId 为空均判「记录不可用」，显式报错且不删文件。字段何时有值：`subagentWtsSessionId` 在子会话建立后写入（建立前允许 0，表示尚未创建；负值不可用）；`childSessionsOpenedByUs` 在开关动作发生时写入；其余字段锁拿到后立即有值。

```json
{
  "sessionId": "b3f1c2d4-…（随机会话 ID，UUIDv4）",
  "hostPid": 4242,
  "hostProcessStartTime": "2026-10-25T14:00:00Z",
  "subagentWtsSessionId": 3,
  "childSessionsOpenedByUs": false,
  "state": "connecting",
  "acquiredAt": "2026-10-25T14:00:01Z"
}
```

- **残留接管判据（每次成功拿到锁后按记录判定，不区分获取方式、不依赖 abandoned 事件）**——owner 死后句柄一关、内核对象可能直接销毁，下一个进程拿到的是普通获取，所以恢复逻辑**只看所有权记录 + 进程存活校验**，每次都跑（全部满足才允许接管并删除残留记录；任一不满足即报 `E_LOCK_STALE_RECORD` 交人工）：
  1. 成功创建命名对象且未返回 `ERROR_ALREADY_EXISTS`（句柄模型下无 `AbandonedMutexException` 独立分支）；
  2. 本 SID 下存在可解析的残留记录 `owner-{SID}.json`；**无残留记录即正常启动，不进接管判据**（别的用户留下的记录本 SID 读不到，也不影响其正常启动）；
  3. 记录中 `hostPid` 当前不存在，或该 PID 存在但其进程启动时间 ≠ `hostProcessStartTime`（PID 复用判据）；
  4. 记录中 `state` ∈ {`disconnected`, `closing`} 或 `acquiredAt` 距今超过 24 小时。
- 接管前后均写结构化日志；接管失败（文件损坏、字段缺失）不静默清理。

### 3.1 代理启动生命周期（临时 HKCU Run 启动项拉起；会话期间挂载、结束即删）

- **地基形态（已定稿，v1.7 修正）**：虚拟桌面 = **同账户 child session**，由 RDP ActiveX `ConnectToChildSession` 属性建立。**子会话能力是系统级开关（`WTSEnableChildSessions`），干净机器默认关闭；开启与关闭都需要管理员权限；改完立即生效，无需重启**（本机实验证实：开启后 child session 可建、主控制台会话全程存活、注销干净；本机曾误当默认开启，实为早年研究所改）。开关生命周期见 §3.1.1。
- **账户前提（硬约束）**：**子会话与宿主是同一个 Windows 账户**（child session 继承当前登录身份）。契约不支持「子会话用别的 Windows 账户」；账户一致性由 §4 握手的对端 SID 校验承担，不一致判 `E_IPC_PEER`，不另立检测入口。
- **拉起方式（v2.7 修正：旧机制经实测证伪后改定）**：**连接前把代理命令行挂载为本账户 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 启动项**（值名 `SoulComputerUse.ChildSessionAgent`），会话结束（正常关闭与拒绝退出两条路径）删除。child session 与宿主同账户、读同一 HKCU 配置单元：其桌面外壳登录时读取 Run 启动项并拉起代理。
  - **旧机制废弃理由（2026-10-27 真机证据）**：RDP `StartProgram`/alternate shell 属性在 child session 下被 Windows 忽略——属性写入并回读为真，但子会话照常启动完整桌面（进程表实测：explorer/sihost/taskhostw 全在、代理未启动、代理日志零新增；对照官方文档：child session 为环回自动登录会话，不提示凭据、无登录屏，文档亦未提供任何 shell 定制入口）。连接参数仍保留该属性的写入与回读验证（未来 Windows 若支持则自动生效），但不再作为拉起依据。
  - **对 v2.6「注册表零落地」与「RunOnce 已废弃」条款的修订**：废弃 RunOnce 的唯一理由（零落地目标）与机制现实冲突——alternate shell 路线已被证伪，同账户、普通权限的宿主没有第三条在子会话内起进程的路（`WTSQueryUserToken`/`CreateProcessAsUser` 需 SYSTEM）。故恢复注册表路线并加约束：**仅在会话生命周期内挂载**（与子会话开关同一对称记账模式），**结束必删**（含拒绝退出路径，`NewSessionHost` 两条退出路径均执行删除），**崩溃残留由代理三闸兜底**（在 console 会话被启动即自拒退出，仅留一条日志，无其他影响）。
  - 值内容 = 代理 exe 路径 + 绑定参数（见 §4），**绝不含任何凭据**；nonce 在挂载窗口内可被同账户进程经注册表读取的风险与旧机制（命令行经连接属性传递）等同，管道 DACL + 对端 SID 校验仍是主认证。
  - 代理不再是外壳（explorer 由系统正常启动），代理启动后无需再拉起 explorer；`shell-missing` 阻塞原因保留定义，正常路径不再触发。
- 安全靠**可审计**：不隐藏、不伪装、不改策略、不碰杀软白名单；注册表写入仅限上述单个 Run 值（HKCU、当前用户、会话期内存在、可审计）；子会话开关经系统 API（UAC 授权后）变更，且只在我们开的那个会话结束时按标记关回。

### 3.1.1 子会话系统开关生命周期（`WTSEnableChildSessions`）

- **探测**：每次启动、建立连接前调 `WTSIsChildSessionsEnabled`（`wtsapi32.dll`）查询，不靠猜。
- **已开启**：不动它，也**不标记为我们开的**（可能是用户或其他软件开启的，不留痕、不关回）。
- **未开启 → 按乙方案开（已定案）**：弹一次 UAC，用户确认后由系统提权调用开启 API；**用户的确认操作即授权**。**提权机制（已定死，实现照抄）**：独立**最小 helper exe**，主程序用 `ShellExecuteEx` 带 `runas` 动词启动它；helper 只做两件事——探测和开关子会话，参数只允许一个动词，不做别的。选它的理由：有明确退出码可判、不装计划任务、不常驻、不改任何系统配置。**helper 路径必须由主程序从自身安装目录推导，不接受外部传路径**。失败路径：
  - UAC 被用户拒绝（`runas` 启动返回 `ERROR_CANCELLED`）→ `E_CHILD_SESSIONS_DECLINED`；
  - 机器无可用管理员会话/提权机会（`ShellExecuteEx` 失败且错误码非 `ERROR_CANCELLED`，标准用户机拿不到管理员凭据输入机会也归此）→ `E_ELEVATION_UNAVAILABLE`，message 说明「这台机器需要管理员先把子会话开一次」；
  - helper 已启动但开启 API 调用失败（按 helper 退出码判定）→ `E_CHILD_SESSIONS_DISABLED`；
  - 三者都**立即退出、不留半开状态、不静默降级、不悄悄换成「提示用户手动开」**。
- **helper 硬约束**：路径必须由主程序从自身安装目录推导的**完整绝对路径**启动，不做 PATH 解析、不接受外部传路径；参数按白名单只认那一个动词，不做别的；任何日志不记路径。
- **硬前置（两条同时满足才允许连接；三态语义，fail-closed）**：
  1. RDP 入站（`fDenyTSConnections`）：**值 = 0 允许；值非 0 禁止**（报 `E_RDP_INBOUND_DENIED`）；**值缺失 = 禁止**，报 `E_RDP_INBOUND_DENIED` 且错误消息**点名缺的是哪个值、写明「显式设为 0 才表示允许」**；
  2. 子会话允许连接（`fDenyChildConnections`）：**值 = 0 允许；值非 0 禁止**（报 `E_CHILD_CONNECTIONS_DENIED`）；**值缺失 = 禁止**，报 `E_CHILD_CONNECTIONS_DENIED` 且消息同样点名值名与「设 0 才允许」。
  值**类型异常**（如字符串而非 DWORD）：显式报错拒绝连接（按对应错误码 + 原因说明），**不得崩溃、不得当允许**。错误消息中的值名是策略值名（可写），不含真实路径。
  **绝不硬连**——子会话能力未开就直连本机 RDP，会接管并注销用户当前主桌面会话；猜错的代价是丢失用户的桌面，缺失语义宁可保守。
- **关回语义**：仅当开关是**本次产品会话开启的**，才在会话结束（§8 关闭流程）时关回；用所有权记录的 `childSessionsOpenedByUs`（布尔）标记；本来就开着的不动。
- 开关开启/关回前后均写结构化日志（event 枚举 `childsessions.enabled` / `childsessions.restored`）。

### 3.2 代理自退三闸（全部 fail-closed，判据拿不到也照样退出）

| 闸 | 判据 | 不满足时 |
|---|---|---|
| 身份闸 | 自身 WTS 会话 ID ≠ `WTSGetActiveConsoleSessionId()`（即不在主桌面控制台会话） | 立即退出（`E_AGENT_WRONG_SESSION`），退出码非 0，日志写明命中身份闸 |
| 认领闸 | 自身 WTS 会话 ID（整数）等于所有权记录中的 `subagentWtsSessionId`，且窗口站 `WinSta0`、桌面 `Default` | 立即退出（`E_AGENT_WRONG_SESSION`），日志写明命中认领闸 |
| 无主闸 | 启动后 30 秒内被宿主通过命名管道认领 | 自行退出（`E_AGENT_UNCLAIMED`），非 0 退出码 |

代理在任何一道闸触发退出时**直接退出**（alternate shell 拉起无注册表启动项可删）。

### 3.3 唯一连接入口与状态链（M2 硬要求）

- **唯一连接入口**：产品侧的 RDP 连接**只允许从一个入口**发起，且该入口**必然先过遗留子会话 sweep**（枚举 → 有遗留先注销 → 注销失败即中止、报 `E_MANUAL_INTERVENTION`，绝不放行）再连接。全仓库**不允许存在第二条绕过 sweep 的连接路径**（结构层验收：`Connect(` 类调用只许出现在该入口文件，别处出现即缺陷）。
- **状态码 → 七态映射（wire 语义，未知值显式报错不猜）**：`0 → disconnected`、`2 → connecting`、`3 → awaiting-credentials`（琥珀等待验证档，由**真实客户端状态**驱动，不许本地伪造）、`1 → ready`。
- **ready 四条判据（全部满足才算 settled，亮度单独不算数——欢迎屏也很亮）**：①`WTSUserName` 有名字；②登录后 ≥2 秒；③连续 3 帧超过亮度阈值；④已登录。
- **登录档不可跳过**：到 ready 时若整条链从未经过 `awaiting-credentials`（`2 → 1` 直达），**判失败**，错误点名「登录档被绕过了，很可能是遗留子会话替你答的」——这条把门禁定案与状态链缝成一件事，端到端验收以此断言。

## 4. IPC 契约（宿主 ↔ 子会话代理，本机命名管道）

- **管道名**：`SoulComputerUse.agent.{sessionId}`（sessionId 为随机会话 ID，不含用户名）。
- **管道安全**：创建时显式 DACL，只放行宿主 SID 与代理 SID；连接后宿主调 `GetNamedPipeClientProcessId` 取对端 PID 并校验其 token SID 与预期账户一致，不匹配即断开（`E_IPC_PEER`）；代理侧对称校验服务端身份。
- **绑定**：宿主建立 RDP 连接时生成一次性 nonce（128 位随机数），随 alternate shell 启动命令行传给代理（`/path/to/project/agent.exe --nonce <REDACTED>`）；**单次有效**——握手成功即作废，代理退出或 30 秒未认领也作废；**绝不入日志与错误消息**。残余风险如实记：nonce 在启动到握手完成的窗口内可被同账户进程经命令行读到；管道的 DACL + SID 校验是主认证，nonce 只做会话绑定，不承担保密职责。
- **消息格式**：单行 JSON + `\n` 分隔，UTF-8；每帧结构：

```json
{ "v": 1, "type": "request", "id": 7, "method": "screenshot", "params": { } }
{ "v": 1, "type": "response", "id": 7, "ok": true, "result": { } }
{ "v": 1, "type": "event", "event": "heartbeat", "data": { "tick": 123 } }
```

- `v` 不匹配即断开并报 `E_IPC_PROTOCOL`；未知 `method` 显式报错，不忽略。
- **握手**：连接后代理先发 `event: hello`（含 `{ "v": 1, "capabilities": ["screen","mouse","keyboard"], "nonce": "<一次性绑定 nonce>" }`），宿主校验 nonce 与对端 SID 后回 `request: hello_ack`；3 秒未完成握手判 `E_AGENT_TIMEOUT`，校验失败判 `E_IPC_PEER` 并断开。
- **心跳**：代理每 5 秒发 `event: heartbeat`；宿主连续 3 个周期（15 秒）未收到判 `E_AGENT_TIMEOUT`，再探活一次进程，进程已死判 `E_AGENT_DEAD`。
- **请求超时**：动作类 10 秒、截图类 15 秒；超时按 §2 错误返回，不重试第二次同 id。
- **断线**：管道断开 → 状态转 `disconnected`，宿主不自动重连。

## 5. MCP 工具清单（JSON Schema）

返回结构统一：成功为工具 result；失败为 `isError: true` + §2 错误对象文本。截图以 MCP image content 返回（`mimeType: "image/png"`，base64），上限以**编码后 PNG 字节数**计：单帧 **5 MiB**（5,242,880 字节），base64 膨胀由发送方承担不占额度，超限报 `E_SCREENSHOT_TOO_LARGE`。所有坐标为子会话**原生物理像素**，原点**左上**。

### 5.1 `get_status`
```json
{ "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false, "required": [] } }
```
返回：`{ "state": "ready", "sessionId": "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f", "authTier": "hello-enrolled", "gateStrength": "strong", "screen": { "width": 1920, "height": 1080 } }`（sessionId 全流程统一为 UUIDv4；`authTier` 为 §7.2 三枚举之一；`gateStrength` 为 `"strong"` / `"login"`）。`system-credential` 档下 Hello 硬件在但未登记时，附枚举字段 `"helloHint": "helloNotEnrolled"`（仅此一值；已登记/无 Hello 硬件时该字段不出现；它是**枚举不是文案**，展示文本由 UI 侧自行映射，改文案不动契约）。

### 5.2 `screenshot`
```json
{ "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false, "required": [] } }
```
返回：image content（PNG，≤5 MiB）。

### 5.3 `mouse_move`
```json
{ "inputSchema": { "type": "object", "properties": { "x": { "type": "integer", "minimum": 0 }, "y": { "type": "integer", "minimum": 0 } }, "additionalProperties": false, "required": ["x", "y"] } }
```
返回：`{ "ok": true }`；越界报 `E_COORD_OUT_OF_RANGE`。

### 5.4 `mouse_click`
```json
{ "inputSchema": { "type": "object", "properties": { "x": { "type": "integer", "minimum": 0 }, "y": { "type": "integer", "minimum": 0 }, "button": { "type": "string", "enum": ["left", "right", "middle"] }, "count": { "type": "integer", "minimum": 1, "maximum": 3 } }, "additionalProperties": false, "required": ["x", "y", "button", "count"] } }
```
返回：`{ "ok": true }`。

### 5.5 `mouse_scroll`
```json
{ "inputSchema": { "type": "object", "properties": { "x": { "type": "integer", "minimum": 0 }, "y": { "type": "integer", "minimum": 0 }, "deltaX": { "type": "integer" }, "deltaY": { "type": "integer" } }, "additionalProperties": false, "required": ["x", "y", "deltaX", "deltaY"] } }
```
返回：`{ "ok": true }`。

### 5.6 `key_type`
```json
{ "inputSchema": { "type": "object", "properties": { "text": { "type": "string", "maxLength": 1000 } }, "additionalProperties": false, "required": ["text"] } }
```
返回：`{ "ok": true }`；`text` 逐字键入，不落日志（§10）。

### 5.7 `key_press`
```json
{ "inputSchema": { "type": "object", "properties": { "keys": { "type": "array", "items": { "type": "string", "enum": ["enter", "esc", "tab", "delete", "backspace", "home", "end", "up", "down", "left", "right", "win", "ctrl", "alt", "shift"] }, "minItems": 1, "maxItems": 5 } }, "additionalProperties": false, "required": ["keys"] } }
```
返回：`{ "ok": true }`。

### 5.8 `start_session`
```json
{ "inputSchema": { "type": "object", "properties": {}, "additionalProperties": false, "required": [] } }
```
无会话且锁空闲时拉起产品窗口（`SoulComputerUse.Host.exe --new-session`），窗口显示 Windows 登录界面，登录仍由操作者本人完成；宿主取得锁后即返回 `{ "ok": true }`——**不等待登录完成**（登录是操作者的手动动作，上限 10 分钟，之后的等待交给 `get_status` 轮询）。已有会话时幂等返回同样结果，不重复开窗。等待登录不是本工具的工作：失败与未就绪按 §2 错误对象返回。**本工具是 MCP 适配器本地工具，不进入 owner/agent 管道的方法集。**

## 6. CLI 命令表（与 MCP 工具一一对应）

`scu`（SoulComputerUse CLI，REPL 形态；单命令模式同参数）：

| CLI 命令 | MCP 工具 | 参数 |
|---|---|---|
| `status` | `get_status` | 无 |
| `shot` | `screenshot` | 无（`--out /path/to/project/shot.png` 可选） |
| `move <x> <y>` | `mouse_move` | x, y（整数） |
| `click <x> <y> --button <b> --count <n>` | `mouse_click` | 同 schema，`--button`、`--count` 均必填显式传 |
| `scroll <x> <y> <dx> <dy>` | `mouse_scroll` | 同 schema |
| `type <text>` | `key_type` | text（≤1000 字符） |
| `key <k1>+<k2>…` | `key_press` | 同 schema 枚举 |
| `start` | `start_session` | 无 |
| `quit` | （本地）触发关闭确认流程 | 无 |

CLI 与 MCP server 默认作为 §3 的 IPC 客户端连接 owner 进程；`start` 与 `quit`、`--help` 同为**本地命令**：不要求现有会话，`start` 拉起宿主窗口，宿主取得单实例锁后即返回——**不等待操作者登录完成**（登录是操作者的手动动作，上限 10 分钟，之后的等待交给 `status` 轮询）。REPL 输出为结构化行（状态 + 错误码），不回显键入正文。

## 7. 凭据流程契约（每次开工系统验证门禁，按设备能力三档）

### 7.1 凭据铁律（先于一切门禁实现）

应用层**永远不接收、不保存、不转发任何凭据**：任何 API、消息、配置、CLI 参数中都不存在密码/ PIN/ 凭据输入项；验证 UI 一律由 Windows 承担，应用只拿到通过/不通过结果。

### 7.2 门禁能力探测（启动时一次，无副作用）

- **探测依据（顺序判定，逐步短路，每步用系统 API、禁副作用试弹）**：
  1. 查 Hello 可用且已登记（`UserConsentVerifier.CheckAvailabilityAsync` 等价查询返回 Available）→ **`hello-enrolled`**；
  2. 否则，`WTSQuerySessionInformation(WTSSessionInfo)` 确认当前会话有交互式登录令牌 → **`system-credential`**（**子会话系统登录验证档**：验证由子会话自身的 Windows 登录界面承担——全新子会话先出登录屏、用户亲手输入密码、输入完成后才进入桌面；应用全程不接触凭据，`CredUIPromptForWindowsCredentials` 等把凭据交回调用方的 API 依旧禁用，见 §7.1 铁律）。**本档必经 `awaiting-credentials` 状态**（窗口显示登录屏等待用户）；遗留已登录子会话必须先注销再连，见下方硬前置。`hello-not-enrolled`（有 Hello 硬件但未登记）归入本档，`get_status` 附枚举引导字段 `helloHint: "helloNotEnrolled"`（见 §5.1，值为枚举非文案）；
  3. 否则 → **`none`**。
- **子会话凭据行为（已定案 A，2026-10-26 用户实测确认）**：**全新子会话建立时，Windows 会先在 RDP 窗口里显示系统登录界面，密码由用户亲手在系统界面输入，输入完成后才进入桌面**。实验全程未写任何凭据属性、未发起任何凭据代输；此前「直接进桌面、不问密码」的判读错误已由用户更正（那次桌面是用户自己登录后才出现的）。属性层事实保留为背景：本机 RDP 控件对 `IMsRdpExtendedSettings` 等四个接口返回 `E_NOINTERFACE`（属性不可设），**但这与登录屏是否出现无因果关系**——登录屏来自子会话自身的 Windows 登录，不需要控件属性。
- **验收记录（2026-10-26 复验，用户原话逐字：「弹了、我输了」）**：方式与证据链——`AxHost` 托管控件、`ConnectToChildSession` 写完**回读为真**、默认设置直连、**全程未设任何凭据属性**；连接前会话表确认无遗留子会话（`leftover-child-session: none`）；主控台 **session 1 全程未变**（连接前后各核一次）；全新会话连接后 RDP 窗口出现 Windows 登录屏，用户亲手输入密码后进入桌面。结论按项目约定以用户所见为准，程序状态码（`connected-state: 2→1`）仅作旁证记录。
- **遗留已登录子会话是门禁失效的关键风险（硬性前置条件）**：只要存在一个**已登录的遗留子会话**，连接就不会要求输入密码，门禁会在用户不知情时失效。因此：
  1. 每次会话结束**必须注销子会话**（§8 关闭流程既有条款，此处强调为门禁前提）；
  2. 开工连接前**必须确认不存在本产品的遗留已登录子会话**；发现则**先注销再连**；
  3. 若注销失败或无法确认，**判失败并报 `E_MANUAL_INTERVENTION`（manual_intervention_required）**，绝不放行到一个「没门禁」的会话上。
- **升级路径（产品说明）**：用户登记 Windows Hello（PIN/指纹）后，探测自动落入 `hello-enrolled` 档、每次开工弹一次 Hello 强验证——**登记即升级，无需改代码**；未登记时产品在 `get_status` 附 `helloHint: "helloNotEnrolled"` 引导。
- **三档**（能用的全支持，按本机实际能力分档，不预设设备）：

| 档 | 条件 | 行为 |
|---|---|---|
| `hello-enrolled` | Hello 可用且本机已登记（PIN/指纹/面容/安全密钥） | 每次开工弹一次系统验证（Hello） |
| `system-credential` | 无 Hello 或未登记（含 `hello-not-enrolled` 归入）；判据：`WTSQuerySessionInformation` 有交互式登录令牌 | **子会话系统登录验证**：每次开工连接全新子会话，Windows 登录界面由用户亲自完成，应用不接触任何凭据；遗留已登录子会话必须先注销再连（见硬前置） |
| `none` | 系统连原生凭据界面都不可用 | **拒绝启动**，报 `E_MANUAL_INTERVENTION`，不静默降级、不自绘密码框 |

- **探测结果进会话状态**：`get_status` 返回 `authTier` 字段（三枚举之一）与 `gateStrength` 字段（`"strong"`（= `hello-enrolled`）/ `"login"`（= `system-credential`，子会话系统登录验证），机器可读；`none` 档拒启不返回会话）。标题栏按枚举渲染档位标注（与设计方规格 `titlebar-gate-tier` 对齐）。
- **不缓存不代输**：验证结果只在当前会话生命周期内有效，会话结束或断线即失效；下次开工重新验证；agent 无任何代输路径。
- **无门禁放宽（已定案：不放宽）**：`none` 档**一律拒绝启动**（fail-closed）——两样验证方式都没有的环境，宁可挡住也不留没锁的入口，任何情况下不放宽。

### 7.3 历史与关联

- `awaiting-credentials` 状态语义：**RDP 窗口正在显示 Windows 登录界面、等待用户本人登录**（子会话系统登录验证，每次全新会话必经）；`hello-enrolled` 档开工时的 Hello 验证同样进本状态。应用与 agent 全程不接触任何凭据。
- 子会话代理拉起方式见 §3.1（RDP alternate shell，注册表零落地），启动参数不含任何凭据材料。
- 历史背景：用户最初要求「每次人工输密码」；后追加「支持本地账号密码验证等方式、不写死 Hello、按设备能力分档」，即本节三档。

## 8. 关闭语义契约（定案 ③）

- 触发路径四条合一：标题栏 ✕、Alt+F4、任务栏右键关闭、系统菜单关闭。
- 确认框：系统模态、警告图标、标题 `结束会话？`、正文**逐字**为「将立即结束当前会话并注销虚拟桌面，未保存的工作将丢失且不可撤销。」（与设计方规格 §5 逐字一致、无任何标记符号）；默认按钮**取消**（Enter/Esc/✕ 均为取消），destructive「结束会话」在右。
- **实现形态（v2.3 补）**：`Func<bool> confirmClose` 回调是**测试缝**，不是产品交互形态。生产必须注入**真系统模态框**（`TaskDialogIndirect`）；对话框**配置抽成纯结构**（标题、正文逐字、默认按钮 id、按钮顺序与标签、图标类型），对配置做数值断言（默认按钮 == 取消、正文与 §5 逐字相等、destructive 在右）；真对话框只留一层薄适配，「默认焦点在取消」「Esc=取消」由用户与验收方实测确认一次。
- 「结束会话」后：转 `closing` → **立即注销**子会话 → 若 `childSessionsOpenedByUs=true` 则**关回系统开关**（§3.1.1）→ 释放单实例锁 → 退出。任一步失败：非 0 退出码 + 结构化错误日志，不得以 0 掩盖残留。

## 9. 坐标模型

- 单位：子会话**原生物理像素**（不随宿主 DPI/缩放换算），原点**左上**，x 向右、y 向下。
- 宿主窗口等比缩放只影响显示；工具调用一律用子会话坐标系。
- 边界：`0 ≤ x < width`、`0 ≤ y < height`（来自 `get_status` 的 `screen` 字段）。

## 10. 脱敏规则与日志白名单

结构化日志字段白名单（仅此 11 项，多一个都不进）：

| 字段 | 说明 |
|---|---|
| `ts` | UTC 时间戳 |
| `level` | 级别 |
| `event` | 事件枚举（如 `lock.acquired`） |
| `sessionId` | 随机会话 ID |
| `state` | 状态机枚举 |
| `code` | §2 错误码 |
| `method` | IPC/MCP/CLI 方法枚举（如 `screenshot`），用于请求关联 |
| `durationMs` | 关键路径耗时 |
| `pid` | 进程号 |
| `width` / `height` | 坐标边界（错误时） |

**绝不入日志/错误响应/仓库**：窗口标题、键入正文、剪贴板内容、截图数据、账户名/邮箱、机器名/用户名、**用户 SID / 账户标识**、绝对路径、**真实文件名**、IP、密钥令牌。键入正文只记长度不记内容；错误消息论「类型」不论「实例」（如只说「所有权记录」，不拼文件名、不拼 SID）。

## 11. 非目标（首期明确不做）

- HTTP/网络控制面：不做任何网络监听与远程调用入口，仅本机管道。
- 密码存储与自动登录：不做凭据保管、代输、记住密码。
- 多会话并发：单实例锁定死全机一个会话。
- 跨平台：仅 Windows 11 x64。
