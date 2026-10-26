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

## 2. 代理启动参数

```text
SoulComputerUse.SessionAgent.exe --scu-bootstrap --session <uuid> --nonce <32-hex>
  --auth-tier <hello-enrolled|system-credential> --gate-strength <strong|login>
```

- `--session` 与 Host 的 `--attach-agent` 必须一致；它是 UUID `D` 格式，不是 Windows 会话号。
- `--nonce` 必须与 Host 的 `--agent-nonce` 完全一致，32 个十六进制字符；不得写入日志或错误消息。
- `--auth-tier` 与 `--gate-strength` 必须成对：`hello-enrolled/strong` 或 `system-credential/login`。
- 代理不接收任何凭据；`none` 档拒绝启动。

## 3. 代理自退闸与退出码

| 退出码 | 含义 |
|---|---|
| 0 | 正常服务后宿主断开 |
| 2 | 命令行非法 |
| 3 | 命中身份闸：进程在操作员控制台会话 |
| 4 | 管道、握手或系统依赖失败 |
| 5 | 25 秒内没有 owner 记录，或 30 秒内没有被宿主认领 |
| 6 | owner 记录不属于本会话、窗口站不是 `WinSta0\Default`，或记录已结束 |

启动顺序：

1. 身份闸：进程会话 ID 必须不同于活动控制台会话。
2. 认领闸：读取当前用户的 owner 记录；记录不存在时最多等待 25 秒；`subagentWtsSessionId = 0` 时视为“等待宿主通过管道认领”。
3. 窗口站闸：必须是 `WinSta0\Default`。
4. 启动 explorer，然后开始监听 agent 管道。
5. 30 秒内没有宿主完成 nonce 握手则退出。

## 4. Host 附加模式

```text
SoulComputerUse.Host.exe --attach-agent <uuid> --agent-nonce <32-hex>
```

- Host 取单实例锁、写 owner 记录、启动 owner 管道，并连接上述 agent 管道。
- 宿主启动后通过 `get_status` 读取代理状态；只有 `ready` 才把 owner 记录推进到 `connecting → ready`。
- 建议先启动代理，再在 25 秒内启动 Host；若 Host 先启动且代理已在运行，代理在记录出现后会尽快开始监听，Host 的连接超时为 5 秒。
- 宿主在握手后读取代理进程的 Windows 会话 ID，并写入 owner 记录的 `subagentWtsSessionId`；代理在记录尚未更新时按“等待管道认领”分支继续。

## 5. 当前未完成项

- Host 还没有通过 RDP alternate shell 自动拉起代理；当前必须在子会话内手动运行代理。
- 代理的 `authTier` 来自启动参数，尚未在代理侧重新探测 Hello/交互式登录事实。
- owner 管道仍使用默认 ACL，未实现显式 DACL、对端 PID/SID 校验、nonce 或心跳。
- agent 管道的跨账户 DACL、真实子会话、安全桌面/锁定桌面尚未实测。
- 关闭时不会主动结束子会话；宿主退出后由用户注销子会话。

## 6. 手动验收步骤

1. 构建：`dotnet build SoulComputerUse.slnx`。
2. 在子会话内启动代理，传入会话 UUID、nonce、认证档与门禁强度。
3. 在控制台启动 Host 附加模式，传入同一个 UUID 与 nonce。
4. 运行 `scu status`，应返回 `state=ready`、`sessionId`、`authTier`、`gateStrength` 与屏幕尺寸。
5. 运行 `scu shot --out <临时文件>`，应得到子会话桌面的 PNG。
6. 运行 `scu click`、`scu type`、`scu key`，确认输入落在子会话而非控制台桌面。
7. 关闭 Host，确认 owner 记录删除、单实例锁释放、代理因管道断开退出。