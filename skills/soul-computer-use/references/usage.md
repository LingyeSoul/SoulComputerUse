# Using the session

The authoritative wire contract is the repository's `docs/interface-contract.md`. This page is the operator and agent quick reference.

## Session states

| State | Meaning | What the agent does |
|---|---|---|
| `starting` | Process is taking the lock and probing the machine; the operator's logon wait happens in this phase, before the owner pipe exists. | Call `scu start` / `start_session` if no window is open; calls answer `E_NOT_READY` (retryable). Wait and poll. |
| `awaiting-credentials` | The product window is showing the Windows login screen. | Stop; the human logs in. Never type credentials. |
| `connecting` | Login finished; the child-session agent handshake is in progress. | Wait. |
| `ready` | Tools are available. | Operate. |
| `blocked` | UAC secure desktop or a non-interactive session is in front. | Stop; the human finishes the prompt. |
| `disconnected` | The pipe or agent went away; no automatic reconnect. | Close and restart the host. |
| `closing` | Teardown is running. | Wait for the process to exit. |

Only `ready` accepts action tools. `blocked` still allows `get_status` and `screenshot`, because the screenshot helps the human decide what the UAC prompt is. `awaiting-credentials` allows only `get_status` and `screenshot`.

## MCP tools

All tools take JSON objects with `additionalProperties: false`. Unknown fields are errors, not ignored.

| Tool | Arguments | Result |
|---|---|---|
| `get_status` | none | `{state, sessionId, authTier, gateStrength, screen:{width,height}}`, plus optional `helloHint` |
| `screenshot` | none | MCP image content, PNG, at most 12 MiB |
| `mouse_move` | `x`, `y` | `{ok:true}` |
| `mouse_click` | `x`, `y`, `button` (`left`/`right`/`middle`), `count` (1–3) | `{ok:true}` |
| `mouse_scroll` | `x`, `y`, `deltaX`, `deltaY` | `{ok:true}` |
| `key_type` | `text` (at most 1000 characters) | `{ok:true}`; the text is never logged |
| `key_press` | `keys` (1–5 of `enter`, `esc`, `tab`, `delete`, `backspace`, `home`, `end`, `up`, `down`, `left`, `right`, `win`, `ctrl`, `alt`, `shift`) | `{ok:true}` |
| `start_session` | none | `{ok:true}` as soon as the host takes the machine lock; it does not wait for logon or read a session — the operator logs on in the product window. Idempotent when a session already exists. |

## CLI

`scu` is the same tool surface as one-shot commands or an interactive `scu>` prompt. Run it without arguments for the prompt.

| Command | Meaning |
|---|---|
| `status` | Print one status line. |
| `shot [--out <path>]` | Without `--out`, print `png=<base64>`. With it, write the PNG and print `ok <path>`. |
| `move <x> <y>` | Move the pointer. |
| `click <x> <y> --button <left\|right\|middle> --count <n>` | Click. Both options are required. |
| `scroll <x> <y> <dx> <dy>` | Scroll at a point. |
| `type <text>` | Type text. In the prompt, everything after `type ` is kept verbatim. |
| `key <k1>+<k2>` | Press a key combination, for example `ctrl+s` or `ctrl+alt+delete`. |
| `start` | Open the product window on a new session; prints `ok` once the host takes the lock, before the operator logs on. Idempotent when a session exists. |
| `quit` | Leave the prompt. It does not close the session. |
| `--help` | Print the command table. |

Output is structured: `ok`, `ok <path>`, a status line, or `<CODE> <message>` on stderr with exit code `1`.

The interactive prompt holds one session for its lifetime: `start` is idempotent and never rebinds a session that has gone away. After a disconnect, exit and run `scu` again.

## Coordinates

- Unit: the child session's **native physical pixels**.
- Origin: top-left. x grows right, y grows down.
- Bounds: `0 <= x < width`, `0 <= y < height`, where `width` and `height` come from `get_status.screen`.
- The product window may scale the picture; tool coordinates never use the window's client coordinates.

Read the screen size again after the session restarts, the display changes, or `E_COORD_OUT_OF_RANGE` appears.

## Error codes

| Code | Meaning | Agent action |
|---|---|---|
| `E_LOCK_HELD` | Another owner holds the machine-wide lock. | Use that session or close it through its window. |
| `E_LOCK_STALE_RECORD` | A leftover ownership record cannot be taken over safely. | Human must inspect/remove the record. |
| `E_LOCK_FOREIGN_HANDLE` | The lock object exists but no live product process holds it. | Restart the machine or exit the suspicious application. |
| `E_AUTH_REQUIRED` | The user refused or failed the gate. | Stop. |
| `E_MANUAL_INTERVENTION` | No usable auth tier, or a leftover logged-on child session. | Log off the child session or configure an auth tier. |
| `E_CHILD_SESSIONS_DISABLED` | The elevation helper could not enable child sessions. | Check the helper and retry. |
| `E_CHILD_SESSIONS_DECLINED` | The UAC consent was dismissed. | Retry and accept it. |
| `E_ELEVATION_UNAVAILABLE` | No administrator session is available. | An administrator must enable child sessions once. |
| `E_CHILD_CONNECTIONS_DENIED` | `fDenyChildConnections` is missing or non-zero. | Set it to DWORD 0. |
| `E_RDP_INBOUND_DENIED` | `fDenyTSConnections` is missing or non-zero. | Set it to DWORD 0. |
| `E_STATE` | The current state rejects this operation. | Re-read `status`; do not force it. |
| `E_NOT_READY` | No session owner is running, or the owner is starting or waiting for the operator's logon. Retryable. | Call `scu start` / `start_session` first if no window is open, then poll `status`. During the logon wait keep polling: this is the normal answer, not an error. |
| `E_AWAITING_CREDENTIALS` | The human has not finished the Windows login. | Wait; never type credentials. |
| `E_DISCONNECTED` | The session is gone and will not reconnect. | Restart the host. |
| `E_BLOCKED` | Secure desktop or non-interactive session. | Stop; human finishes the prompt. |
| `E_CLOSING` | Teardown is running. | Wait for exit. |
| `E_AGENT_TIMEOUT` | The child-session agent missed heartbeats. | Close and restart the host. |
| `E_AGENT_DEAD` | The child-session agent exited. | Close and restart the host. |
| `E_IPC_PROTOCOL` | A pipe frame or version did not match the contract. | Restart both sides; do not retry the same frame. |
| `E_IPC_PEER` | Peer PID/SID or the handshake nonce did not match. | Stop; do not bypass the check. |
| `E_INPUT_INVALID` | An argument was invalid. | Fix the named field. |
| `E_COORD_OUT_OF_RANGE` | The point is outside the child desktop. | Re-read `screen` and retry within bounds. |
| `E_SCREENSHOT_TOO_LARGE` | The encoded PNG exceeded 12 MiB. | Report it; the limit is raised only by changing the contract. |
| `E_AGENT_ERROR` | The child-session action failed. | Read the enumerated reason (`uac-shield`, `window-gone`, `input-rejected`) and stop on security reasons. |
| `E_HOST_LAUNCH_FAILED` | The host could not be launched from the caller's environment: missing host executable, elevated caller, caller off the interactive console session, refused process creation, host exited before taking the lock, or the wait limit. Not retryable. | Read the message, fix the named cause, and only then retry. An elevated agent needs the human to run `scu start` from a normal-privilege terminal. |

## Operating loop

1. If no product window is open, call `scu start` / `start_session` first; `E_NOT_READY` here is retryable (the session is starting or waiting for the operator's logon).
2. Read `get_status` or `scu status`.
3. If not `ready`, follow the state table; do not send input.
4. Take a screenshot before a non-trivial interaction and use it to choose coordinates.
5. Act with one tool at a time. Re-read `status` after a failure.
6. Stop on `blocked`, `awaiting-credentials`, `disconnected`, or any security-related `E_AGENT_ERROR`.
7. Close through the product window. Killing the host skips child-session logoff and switch restoration.

## Security boundaries

- The product, CLI, MCP server, and agent never accept a credential. There is no password field, no auto-login, and no credential persistence.
- The agent only runs inside the child session. It refuses to start in the console session.
- The owner pipe is local-only, with an explicit DACL, peer SID checks, and a per-session nonce. There is no network listener.
- Screenshots and typed text are sensitive. Do not put them in logs, issues, or a repository.
- Do not use the product to bypass UAC, a lock screen, or an organization's policy.
