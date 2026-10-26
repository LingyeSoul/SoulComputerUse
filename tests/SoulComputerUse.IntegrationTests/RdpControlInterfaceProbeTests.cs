using System;
using System.Runtime.InteropServices;
using System.Threading;
using SoulComputerUse.Windows.Rdp.SessionLaunch;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Probes which interfaces the control really answers when it is hosted by <c>AxHost</c>, rather than created
/// through the raw <c>CoCreateInstance</c> route the earlier experiment used. The two routes hand back
/// different wrappers, and the credential-prompt settings only exist on the non-scriptable interfaces, so
/// whether they can be written decides if the client prompts for a password itself or leaves the logon to the
/// child session's own Windows logon screen.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpControlInterfaceProbeTests
{
    private const string RdpClientNonScriptable3 = "b3378d90-0728-45c7-8ed7-b06a63b9a6f3";
    private const string RdpClientNonScriptable5 = "4f6996d5-f0eb-4128-bbcc-36b7e7df1e00";
    private const string RdpExtendedSettings = "302d8188-0052-4807-806a-362b628f9ac5";

    [TestMethod]
    public void TheHostedControlReportsWhichNonScriptableInterfacesItAnswers()
    {
        string report = RunProbe();
        Console.WriteLine(report);

        // The probe never fails on an unavailable interface: that answer is exactly what it is measuring.
        Assert.IsTrue(report.Contains("ocx=", StringComparison.Ordinal), report);
    }

    private static string RunProbe()
    {
        string report = "ocx=unavailable";
        Thread thread = new(() =>
        {
            try
            {
                using RdpControlSurface surface = new();
                object ocx = surface.Ocx;
                IntPtr unknown = Marshal.GetIUnknownForObject(ocx);
                try
                {
                    report = "ocx=" + ocx.GetType().Name
                        + " nonscriptable3=" + Answers(unknown, RdpClientNonScriptable3)
                        + " nonscriptable5=" + Answers(unknown, RdpClientNonScriptable5)
                        + " extendedsettings=" + Answers(unknown, RdpExtendedSettings);
                }
                finally
                {
                    _ = Marshal.Release(unknown);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                report = "ocx=unavailable error=" + error.GetType().Name + ": " + error.Message;
            }
        })
        {
            IsBackground = true,
            Name = "SoulComputerUse interface probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return report;
    }

    private static string Answers(IntPtr unknown, string interfaceId)
    {
        Guid guid = new(interfaceId);
        int result = Marshal.QueryInterface(unknown, in guid, out IntPtr probe);
        if (result != 0)
        {
            return "no(0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture) + ")";
        }

        _ = Marshal.Release(probe);
        return "yes";
    }
}