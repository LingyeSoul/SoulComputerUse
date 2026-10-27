using System;
using System.IO;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The child-session agent must start windowless: the operator watches the session's desktop through the
/// product window, so a console-subsystem build would put a black console window in front of every
/// session the child session starts.
/// </summary>
/// <remarks>
/// The check reads the built executable's PE header instead of the project file. A project setting the
/// build does not honour would pass a text check and still ship the window; this reads exactly the
/// artifact the child session runs.
/// </remarks>
[TestClass]
public sealed class AgentWindowSubsystemTests
{
    /// <summary>The agent executable, copied next to this test assembly by its project reference.</summary>
    private const string AgentFileName = "SoulComputerUse.SessionAgent.exe";

    /// <summary>IMAGE_SUBSYSTEM_WINDOWS_GUI: Windows creates no console window for this process.</summary>
    private const int WindowsGuiSubsystem = 2;

    /// <summary>The offset of the PE header pointer in the DOS header.</summary>
    private const int PeOffsetFieldInDosHeader = 0x3C;

    /// <summary>The four bytes <c>PE\0\0</c> that open a PE header.</summary>
    private const int PeSignature = 0x00004550;

    /// <summary>The PE signature plus the COFF header, between the pointer and the optional header.</summary>
    private const int HeaderSizeBeforeOptionalHeader = 24;

    /// <summary>IMAGE_OPTIONAL_HEADER's Subsystem field, at the same offset in PE32 and PE32+.</summary>
    private const int SubsystemOffsetInOptionalHeader = 68;

    [TestMethod]
    public void TheAgentBinaryIsAWindowSubsystemProcess()
    {
        string agentPath = Path.Combine(AppContext.BaseDirectory, AgentFileName);
        Assert.IsTrue(
            File.Exists(agentPath),
            $"The agent executable ({AgentFileName}) is missing next to the tests, so the subsystem it is built with cannot be checked.");

        int subsystem = ReadSubsystem(agentPath);

        Assert.AreEqual(
            WindowsGuiSubsystem,
            subsystem,
            "The agent must be built as a window-subsystem (WinExe) process: the child session starts it on the desktop the operator watches through the product window, and a console-subsystem process makes Windows open a black console window there.");
    }

    /// <summary>Reads the IMAGE_OPTIONAL_HEADER.Subsystem field of one PE image.</summary>
    /// <exception cref="AssertFailedException">The file is not a PE image, so nothing about the process can be judged.</exception>
    private static int ReadSubsystem(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        stream.Position = PeOffsetFieldInDosHeader;
        int peOffset = reader.ReadInt32();
        stream.Position = peOffset;

        int signature = reader.ReadInt32();
        if (signature != PeSignature)
        {
            throw new AssertFailedException(
                $"'{Path.GetFileName(path)}' does not open with a PE header, so the process subsystem it is built with cannot be read.");
        }

        stream.Position = peOffset + HeaderSizeBeforeOptionalHeader + SubsystemOffsetInOptionalHeader;
        return reader.ReadUInt16();
    }
}