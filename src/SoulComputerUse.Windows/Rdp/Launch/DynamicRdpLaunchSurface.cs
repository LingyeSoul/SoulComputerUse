using System;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// Writes the alternate-shell properties on the raw remote desktop control, through
/// <c>AdvancedSettings2</c> the way the control's scriptable surface exposes them.
/// </summary>
/// <remarks>
/// The adapter reads <c>AdvancedSettings2</c> once and keeps the object it returned; each property
/// access then goes straight to that object. A control that answers with a different object on every
/// read would make a read-back check meaningless, and holding the object also keeps the writes in the
/// same in-memory connection configuration.
/// </remarks>
public sealed class DynamicRdpLaunchSurface : IRdpLaunchSurface
{
    private readonly object _advancedSettings;

    /// <summary>Creates the adapter over the raw control object the ActiveX host attached.</summary>
    /// <exception cref="ArgumentNullException">The control object is null.</exception>
    public DynamicRdpLaunchSurface(object ocx)
    {
        ArgumentNullException.ThrowIfNull(ocx);

        dynamic client = ocx;
        _advancedSettings = client.AdvancedSettings2;
    }

    /// <inheritdoc />
    public string StartProgram
    {
        get
        {
            dynamic settings = _advancedSettings;
            return settings.StartProgram;
        }

        set
        {
            dynamic settings = _advancedSettings;
            settings.StartProgram = value;
        }
    }

    /// <inheritdoc />
    public string WorkingDirectory
    {
        get
        {
            dynamic settings = _advancedSettings;
            return settings.WorkingDirectory;
        }

        set
        {
            dynamic settings = _advancedSettings;
            settings.WorkingDirectory = value;
        }
    }
}
