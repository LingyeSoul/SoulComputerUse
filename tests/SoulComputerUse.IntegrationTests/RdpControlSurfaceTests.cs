using System;
using System.Threading;
using SoulComputerUse.Windows.Rdp.SessionLaunch;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Probes whether the hosted remote desktop control can be created at all on this machine: the host needs a
/// real handle and a real control object before it can configure the alternate shell or attach the control
/// as the product window's picture surface.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class RdpControlSurfaceTests
{
    [TestMethod]
    public void TheHostedControlCreatesAHandleAndAControlObject()
    {
        Exception? failure = null;
        nint handle = 0;
        bool attached = false;

        Thread thread = new(() =>
        {
            try
            {
                using RdpControlSurface surface = new();
                handle = surface.WindowHandle;
                _ = surface.Ocx;
                attached = true;
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error;
            }
        })
        {
            IsBackground = true,
            Name = "SoulComputerUse rdp control probe",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            Assert.Fail("The control could not be created: " + failure.GetType().Name + ": " + failure.Message);
        }

        Assert.IsTrue(attached, "The control object must be attached before the alternate shell can be written.");
        Assert.AreNotEqual(nint.Zero, handle, "The control must expose a real window handle to become the picture surface.");
    }
}