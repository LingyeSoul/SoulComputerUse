using System;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Guards the start surface published by the interface contract. The document is what agents and
/// operators are pointed at, and nothing in the build compiles it, so a start surface that exists in
/// code but is missing from the document is invisible; worse, a retired sentence that told callers a
/// CLI or MCP process starts the session by itself would send them back to the old behaviour.
/// </summary>
[TestClass]
public sealed class ContractDocumentTests
{
    /// <summary>The section 2 row that publishes the launch failure code, as the document states it.</summary>
    private const string LaunchFailureRow =
        "| `E_HOST_LAUNCH_FAILED` | 宿主进程未能从调用方环境中拉起（安装目录缺宿主可执行文件、调用方不在交互控制台会话、调用方已提权、进程创建失败、或宿主在取得锁之前退出） | false | starting |";

    [TestMethod]
    public void TheContractPublishesTheStartSurfaceAndTheLaunchFailureCode()
    {
        string contract = ReadRepositoryFile(Path.Combine("docs", "interface-contract.md"));

        Assert.Contains("v2.8", contract, "The contract must carry the version marker that lands the start surface.");
        Assert.Contains(LaunchFailureRow, contract, "The launch failure code must be published as a section 2 row.");
        Assert.Contains("E_NOT_READY", contract, "The retryable answer for a starting owner must stay published.");
        Assert.Contains("### 5.8 `start_session`", contract, "The adapter-local start tool needs its own section 5 entry.");
        Assert.Contains("| `start` | `start_session` | 无 |", contract, "The CLI command table must map start to start_session.");
        Assert.Contains("**本地命令**", contract, "The contract must say start is local and needs no existing session.");
        Assert.DoesNotContain(
            "仅当无 owner 且锁空闲时才自行起会话并持锁",
            contract,
            "A CLI or MCP process must never again be promised that it starts the session on its own.");
        Assert.DoesNotContain(
            "连不上且锁空闲时才由自己起会话并持锁",
            contract,
            "The topology must never again promise a self-started session from a free lock.");
    }

    /// <summary>Reads one repository file by walking up from the test output directory.</summary>
    private static string ReadRepositoryFile(string relativePath)
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current is not null)
        {
            string candidate = Path.Combine(current.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            current = current.Parent;
        }

        throw new FileNotFoundException(
            $"'{relativePath}' was not found from the test output directory, so this check would pass by checking nothing.");
    }
}