using SoulComputerUse.Core.Protocol;

namespace SoulComputerUse.Mcp;

/// <summary>
/// The tool one <c>tools/call</c> request names: a session tool that goes over the owner pipe, or an
/// adapter-local tool that never does.
/// </summary>
/// <remarks>
/// Exactly one of the two members is non-null. The two surfaces stay separate because a local tool is
/// a process action, not an IPC method; a selection that names neither or both is a programming error.
/// </remarks>
internal sealed record McpToolSelection(SessionTool? SessionTool, McpLocalTool? LocalTool);
