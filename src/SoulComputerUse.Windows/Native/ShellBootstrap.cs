using System;
using System.Diagnostics;
using System.IO;

namespace SoulComputerUse.Windows.Native;

/// <summary>
/// Starts the interactive shell of the current session. When the RDP connection launches the
/// agent as its alternate shell, nothing else brings up wallpaper, icons and the taskbar, so the
/// agent has to start explorer itself.
/// </summary>
public static class ShellBootstrap
{
    /// <summary>Starts explorer.exe in the calling session.</summary>
    public static void StartExplorer()
    {
        string windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(windowsDirectory))
        {
            throw new InvalidOperationException("The Windows directory could not be resolved for the current account.");
        }

        string explorerPath = Path.Combine(windowsDirectory, "explorer.exe");
        if (!File.Exists(explorerPath))
        {
            throw new FileNotFoundException($"The interactive shell was not found at {explorerPath}.", explorerPath);
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = explorerPath,
            UseShellExecute = false,
        };
        using Process shell = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Starting {explorerPath} did not return a process object.");
    }
}