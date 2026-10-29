# 子会话代理运行说明（当前实现）

> 本文记录当前代码的真实运行形态与手动验收步骤，不修改 `interface-contract.md` 的契约。
> 示例值均为占位符，不含真实路径、账户、机器名或密钥。

## 1. 进程与管道拓扑

```text
CLI / MCP ── SoulComputerUse.owner ── Host（owner，持单实例锁）
                                      │
                                      └── SoulComputerUse.agent.{sessionId} ── SessionAgent（子会话内）
```

- owner 管道承载七个工具，由 `SessionOwnerService` 与 `NamedPipeSessionServer` 提供。
- agent 管道承载同一组工具，由 `AgentPipeServer` 提供；连接前校验当前用户 SID、显式 DACL 与 nonce。
- 两条管道的名字、握手和信任边界不同，不得混用。
- 代理进程以**窗口子系统**（`WinExe`）构建：子会话的 `Run` 启动项拉起它时不会出现控制台窗口；只有手工从终端启动时，代理才附加到调用方的控制台，标准错误才有读者。
- 工具调用不落盘：截图经管道以 base64 帧返回，CLI 仅在显式指定 `--out` 时写文件。会话生命周期内的磁盘写入只有所有权记录（每次状态迁移一次）与代理诊断日志（每个事件一行）。

## 1.1 画面路径

- **产品窗口的画面来自 RDP 控件本身**：`RdpControlHost`（MsTscAx ActiveX）在 STA 线程创建，其窗口句柄交给 `ProductWindow.AttachPictureSurface`，按 `PictureBounds` 等比摆放，四周留黑边。控件直接解码远端视频流，因此 `awaiting-credentials` 阶段的 Windows 登录界面也由它渲染，用户在产品窗口内亲手登录。
- **工具调用的截图来自子会话内的代理**：`screenshot` 经 owner 管道 → agent 管道 → 子会话内 GDI 抓屏，返回子会话原生物理像素。这条路径不依赖宿主窗口是否可见、是否被遮挡，坐标与 §9 一致。
- `blocked` 与 `awaiting-credentials` 阶段没有可用的代理，此时宿主改用控件窗口抓帧（`PrintWindow` 优先，全黑或不可用时退回屏幕区域并做遮挡检查）；这两种状态下 `screenshot` 仍按 §1.3 可用，输入类工具拒绝。
- 三者的取舍：RDP 控件负责「人看到的画面」，代理负责「工具拿到的像素」，宿主抓帧只覆盖代理尚不存在的两个状态。不要把三者混成一条路径。

## 1.2 启动器（CLI / MCP 拉起宿主）

- `scu start` 与 MCP 本地工具 `start_session` 共用 `HostProcessLauncher`：无 owner 且锁空闲时，从自身安装目录推导并拉起 `SoulComputerUse.Host.exe --new-session`（绝不走 PATH、不接受外部传路径）；已有 owner（管道应答）时幂等返回 `ok`，不重复开窗。
- 返回时机：宿主持到单实例锁即返回 `ok`（每 200ms 轮询、上限 30 秒）；**不等待登录完成**——登录是操作者在窗口里的手动动作（上限 10 分钟），之后的就绪等待交给 `status` / `get_status` 轮询。宿主在取锁前退出、或 30 秒内未取到锁，按 `E_HOST_LAUNCH_FAILED` 失败（消息带退出码或超时秒数）。
- 启动中语义：锁在、管道无，且本账户所有权记录显示持有者进程存活、启动时间匹配、状态 ∈ {`starting`, `connecting`}（登录等待期）时，发现与启动都按 `E_NOT_READY`（retryable）回答；记录缺失、不可读或持有者已死时维持 `E_LOCK_HELD`。此路径只读记录，不接管、不删除、不改写。
- 句柄隔离：宿主由 `HostProcessStarter` 用 `CreateProcessW` 创建——`bInheritHandles=false`、`STARTF_USESTDHANDLES` 且三个标准句柄均为 `INVALID_HANDLE_VALUE`、新进程、新进程组，因此不继承调用者的 stdout/stderr。理由：宿主若继承 MCP 的 stdout，MCP 退出后该管道迟迟不到 EOF；宿主若共享 `scu start` 的控制台，Ctrl+C 或关闭终端可能连带杀掉宿主、留下已登录子会话。代价如实接受：宿主启动失败只弹产品窗口对话框，启动器只拿得到退出码。
- 作业对象脱离：调用者在 job 对象中时，宿主以 `CREATE_BREAKAWAY_FROM_JOB` 创建，避免 job 关闭（agent 退出）带走宿主、留下已登录子会话；job 不允许脱离时显式失败，不静默留下风险。
- 两个拒绝：调用者不在活动控制台会话（`ConsoleSessionGuard`）或已提权（`ElevationGuard`）时，`start` / `start_session` 按 `E_HOST_LAUNCH_FAILED` 直接失败；提权调用会让 Windows 跳过操作者的 UAC 确认（契约 §3.1.1 视该确认为授权动作），消息要求以普通权限在控制台会话里重试。

## 2. 代理启动参数

```text
SoulComputerUse.SessionAgent.exe --scu-bootstrap --session <uuid> --nonce <32-hex>
  [--auth-tier <hello-enrolled|system-credential> --gate-strength <strong|login>]
```

- `--session` 与 Host 的 `--attach-agent` 必须一致；它是 UUID `D` 格式，不是 Windows 会话号。
- `--nonce` 必须与 Host 的 `--agent-nonce` 完全一致，32 个十六进制字符；不得写入日志或错误消息。
- 档位参数可省略，生产启动命令省略；代理在子会话内自行探测档位。若声明了档位，则必须成对（`hello-enrolled/strong` 或 `system-credential/login`）且与探测结果一致，否则以退出码 2 拒绝启动。
- 代理不接收任何凭据；`none` 档拒绝启动（退出码 7）。

## 3. 代理自退闸与退出码

| 退出码 | 含义 |
|---|---|
| 0 | 正常服务后宿主断开 |
| 2 | 命令行非法，或声明的档位与探测结果不一致 |
| 3 | 命中身份闸：进程在操作员控制台会话 |
| 4 | 管道、握手或系统依赖失败 |
| 5 | 25 秒内没有 owner 记录，或 30 秒内没有被宿主认领 |
| 6 | owner 记录不属于本会话、窗口站不是 `WinSta0\Default`，或记录已结束 |
| 7 | 门禁探测结果为 `none`，按契约拒绝启动 |

启动顺序：

1. 身份闸：进程会话 ID 必须不同于活动控制台会话。
2. 认领闸：读取当前用户的 owner 记录；记录不存在时最多等待 25 秒；`subagentWtsSessionId = 0` 时视为“等待宿主通过管道认领”。
3. 窗口站闸：必须是 `WinSta0\Default`。
4. 门禁探测：Hello 已登记 → `hello-enrolled/strong`；否则有交互式登录令牌 → `system-credential/login`；否则 `none` 拒绝启动。探测失败按 fail-closed 处理。
5. 开始监听 agent 管道。不启动 explorer：子会话的桌面外壳由系统在登录时启动，代理本身就是被该外壳读取 `Run` 启动项拉起的。
6. 30 秒内没有宿主完成 nonce 握手则退出。

## 4. Host 附加模式

```text
SoulComputerUse.Host.exe --attach-agent <uuid> --agent-nonce <32-hex>
```

- Host 取单实例锁、写 owner 记录、启动 owner 管道，并连接上述 agent 管道。
- 宿主启动后通过 `get_status` 读取代理状态；只有 `ready` 才把 owner 记录推进到 `connecting → ready`。
- 建议先启动代理，再在 25 秒内启动 Host；若 Host 先启动且代理已在运行，代理在记录出现后会尽快开始监听，Host 的连接超时为 5 秒。
- 宿主在握手后读取代理进程的 Windows 会话 ID，并写入 owner 记录的 `subagentWtsSessionId`；代理在记录尚未更新时按“等待管道认领”分支继续。

## 4.1 新建会话模式

```text
SoulComputerUse.Host.exe --new-session
```

- 不需要任何参数：会话 UUID 与绑定 nonce 由 Host 生成，不接受外部传入。
- 三段时序：
  1. **连接前**：取单实例锁 → 读/接管所有权记录 → 写 `starting` → 探测档位（`none` 拒绝）→ 遗留子会话 sweep → 子会话开关（必要时提权并回读）→ 写 alternate shell 属性。
  2. **连接**：唯一入口发起 RDP 连接；窗口显示 Windows 登录界面，标题栏进入 `awaiting-credentials`；等待期间持续泵消息，因此登录界面正常绘制。
  3. **登录后**：用户亲手登录 → 代理作为 alternate shell 启动 → 连接 agent 管道 → 写 `subagentWtsSessionId` → 启动 owner 管道 → `connecting` → `ready`。
- 登录档不可跳过：控件若从 `connecting` 直跳 `connected`（`2 → 1`），判 `E_STATE` 并中止，不进入登录后段。
- 等待上限 10 分钟；超时按 `E_AGENT_TIMEOUT` 中止。

## 5. 当前未完成项

- 代理已在子会话内自行探测档位；但 Hello 探测的系统判定尚未接入，当前注入的 Hello 事实恒为「不可用」，因此实际只会落入 `system-credential` 或 `none`，不会自报 `hello-enrolled`、也不会给出 `helloNotEnrolled`。
- owner 管道已使用显式 DACL（仅当前用户与 LocalSystem），并做双向对端 SID 校验与 8 MiB 单帧上限；仍没有 nonce、心跳与消息加密。
- agent 管道的跨账户 DACL、真实子会话、安全桌面/锁定桌面尚未实测。
- `blocked` 与 `awaiting-credentials` 下的宿主截图（`Rdp/Capture`）尚未接入 owner 管道的 `screenshot`。
- 真实注销子会话与真实关回开关均未在真机验证；测试只用假会话表与假开关。
- 结束会话时不会主动结束子会话之外的任何会话；注销目标只允许本产品子会话。

## 6. 手动验收步骤

1. 构建：`dotnet build SoulComputerUse.slnx`。
2. 启动新建会话模式：`SoulComputerUse.Host.exe --new-session`；标题栏应显示「正在连接…」后转为「等待验证」。
3. 在产品窗口内看到 Windows 登录界面，亲手输入该账户的密码并登录。
4. 登录后窗口应出现远端子会话桌面（桌面上没有代理的控制台窗口），标题栏转为就绪态。
5. 运行 `scu status`，应返回 `state=ready`、`sessionId`、`authTier`、`gateStrength` 与屏幕尺寸。
6. 运行 `scu shot --out <临时文件>`，应得到子会话桌面的 PNG。
7. 运行 `scu click`、`scu type`、`scu key`，确认输入落在子会话而非控制台桌面。
8. 关闭 Host 并确认，检查 owner 记录删除、单实例锁释放、子会话已注销、代理已退出。
9. 若要复现失败原因，设置 `SOULCOMPUTERUSE_DIAGNOSTICS=1` 再运行，stderr 会附上异常的完整内层链。