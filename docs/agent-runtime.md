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

## 1.1 画面路径

- **产品窗口的画面来自 RDP 控件本身**：`RdpControlHost`（MsTscAx ActiveX）在 STA 线程创建，其窗口句柄交给 `ProductWindow.AttachPictureSurface`，按 `PictureBounds` 等比摆放，四周留黑边。控件直接解码远端视频流，因此 `awaiting-credentials` 阶段的 Windows 登录界面也由它渲染，用户在产品窗口内亲手登录。
- **工具调用的截图来自子会话内的代理**：`screenshot` 经 owner 管道 → agent 管道 → 子会话内 GDI 抓屏，返回子会话原生物理像素。这条路径不依赖宿主窗口是否可见、是否被遮挡，坐标与 §9 一致。
- `blocked` 与 `awaiting-credentials` 阶段没有可用的代理，此时宿主改用控件窗口抓帧（`PrintWindow` 优先，全黑或不可用时退回屏幕区域并做遮挡检查）；这两种状态下 `screenshot` 仍按 §1.3 可用，输入类工具拒绝。
- 三者的取舍：RDP 控件负责「人看到的画面」，代理负责「工具拿到的像素」，宿主抓帧只覆盖代理尚不存在的两个状态。不要把三者混成一条路径。

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
5. 启动 explorer，然后开始监听 agent 管道。
6. 30 秒内没有宿主完成 nonce 握手则退出。

## 4. Host 附加模式

```text
SoulComputerUse.Host.exe --attach-agent <uuid> --agent-nonce <32-hex>
```

- Host 取单实例锁、写 owner 记录、启动 owner 管道，并连接上述 agent 管道。
- 宿主启动后通过 `get_status` 读取代理状态；只有 `ready` 才把 owner 记录推进到 `connecting → ready`。
- 建议先启动代理，再在 25 秒内启动 Host；若 Host 先启动且代理已在运行，代理在记录出现后会尽快开始监听，Host 的连接超时为 5 秒。
- 宿主在握手后读取代理进程的 Windows 会话 ID，并写入 owner 记录的 `subagentWtsSessionId`；代理在记录尚未更新时按“等待管道认领”分支继续。

## 5. 当前未完成项

- Host 还没有通过 RDP alternate shell 自动拉起代理；alternate shell 的命令行拼装与控制属性写入（含回读校验）已在 `Rdp/Launch` 实现，但尚未接入 Host 的连接流程。当前必须在子会话内手动运行代理。
- 启动时序已完成三段拆分（连接前 / 等待登录 / 登录后），但三段的生产端口尚未在 Host 中组合；Host 的完整新建会话流程仍在开发中。
- 代理已在子会话内自行探测档位；但 Hello 探测的系统判定尚未接入，当前注入的 Hello 事实恒为「不可用」，因此实际只会落入 `system-credential` 或 `none`，不会自报 `hello-enrolled`、也不会给出 `helloNotEnrolled`。
- owner 管道已使用显式 DACL（仅当前用户与 LocalSystem），并做双向对端 SID 校验与 8 MiB 单帧上限；仍没有 nonce、心跳与消息加密。
- agent 管道的跨账户 DACL、真实子会话、安全桌面/锁定桌面尚未实测。
- 关闭流程已接入 Host：确认关闭后进入 closing、注销子会话、关回开关（仅当本会话开启过）、释放锁；但子会话开关的开启路径尚未实现，因此 `childSessionsOpenedByUs` 目前恒为 false。
- 真实注销子会话与真实关回开关均未在真机验证；测试只用假会话表与假开关。
- 结束会话时不会主动结束子会话之外的任何会话；注销目标只允许本产品子会话。

## 6. 手动验收步骤

1. 构建：`dotnet build SoulComputerUse.slnx`。
2. 在子会话内启动代理，只需传入会话 UUID 与 nonce（档位由代理自行探测）。
3. 在控制台启动 Host 附加模式，传入同一个 UUID 与 nonce。
4. 运行 `scu status`，应返回 `state=ready`、`sessionId`、`authTier`、`gateStrength` 与屏幕尺寸。
5. 运行 `scu shot --out <临时文件>`，应得到子会话桌面的 PNG。
6. 运行 `scu click`、`scu type`、`scu key`，确认输入落在子会话而非控制台桌面。
7. 关闭 Host，确认 owner 记录删除、单实例锁释放、代理因管道断开退出。