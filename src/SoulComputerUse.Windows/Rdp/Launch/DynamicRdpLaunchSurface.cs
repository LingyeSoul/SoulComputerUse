using System;

namespace SoulComputerUse.Windows.Rdp.Launch;

/// <summary>
/// Writes the alternate-shell properties on the raw remote desktop control.
/// </summary>
/// <remarks>
/// <para>
/// The two properties are not on <c>AdvancedSettings</c>: a hosted control exposes the program to start and
/// its working directory through <c>SecuredSettings</c> (<c>StartProgram</c> and <c>WorkDir</c>). The adapter
/// therefore resolves the newest <c>SecuredSettings</c> member the control answers, keeps the object it
/// returned, and reads back from that same object so a verification cannot compare against a different one.
/// </para>
/// <para>
/// The member is resolved late on purpose. The scriptable surface is what a hosted control reliably
/// exposes, while the typed interfaces differ between control versions; binding early would tie this file to
/// one version and fail on another.
/// </para>
/// <para>
/// A control that answers none of the members is an explicit failure, never a silent "nothing to configure":
/// without these properties the child session runs the usual shell and no agent is ever started.
/// </para>
/// </remarks>
public sealed class DynamicRdpLaunchSurface : IRdpLaunchSurface
{
    private readonly object _securedSettings;

    /// <summary>Creates the adapter over the raw control object the ActiveX host attached.</summary>
    /// <exception cref="ArgumentNullException">The control object is null.</exception>
    /// <exception cref="InvalidOperationException">The control exposes no secured-settings member.</exception>
    public DynamicRdpLaunchSurface(object ocx)
    {
        ArgumentNullException.ThrowIfNull(ocx);

        dynamic client = ocx;
        _securedSettings = ResolveSecuredSettings(client);
    }

    /// <inheritdoc />
    public string StartProgram
    {
        get
        {
            dynamic settings = _securedSettings;
            return settings.StartProgram;
        }

        set
        {
            dynamic settings = _securedSettings;
            settings.StartProgram = value;
        }
    }

    /// <inheritdoc />
    public string WorkingDirectory
    {
        get
        {
            dynamic settings = _securedSettings;

            // The control calls this property WorkDir; the interface uses the clearer name its callers use.
            return settings.WorkDir;
        }

        set
        {
            dynamic settings = _securedSettings;
            settings.WorkDir = value;
        }
    }

    /// <summary>
    /// Returns the newest secured-settings member the control answers. The versions differ only by the
    /// members they add, so the newest one that resolves still carries the two properties written here.
    /// </summary>
    private static object ResolveSecuredSettings(dynamic client)
    {
        string[] members = ["SecuredSettings3", "SecuredSettings2", "SecuredSettings"];
        foreach (string member in members)
        {
            object? settings = TryReadMember(client, member);
            if (settings is not null)
            {
                return settings;
            }
        }

        throw new InvalidOperationException(
            "The remote desktop control exposes no secured-settings member, so the alternate shell cannot be configured and the child session must not connect.");
    }

    private static object? TryReadMember(dynamic client, string member)
    {
        try
        {
            object boxed = client;
            return boxed.GetType().InvokeMember(
                member,
                System.Reflection.BindingFlags.GetProperty,
                binder: null,
                target: boxed,
                args: null,
                culture: System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return null;
        }
    }
}