using System;
using System.Runtime.InteropServices;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>
/// Owns one instance of the remote desktop control and answers which interfaces it really implements.
/// </summary>
/// <remarks>
/// <para>
/// Two different creation routes are measured here on purpose. The raw <c>IUnknown</c> route creates
/// the class directly; the type-library route hands the process a runtime callable wrapper. A wrapper
/// answers <c>QueryInterface</c> only for the interfaces of its metadata, so a raw <c>E_NOINTERFACE</c>
/// alone proves nothing about whether the control would ask for credentials — that is a property of
/// the type-library interface, and the predecessor product used exactly that route and did prompt.
/// </para>
/// <para>
/// The class identifier is pinned to the same one the predecessor hosted through its ActiveX host,
/// so both products measure the same control.
/// </para>
/// </remarks>
internal sealed class RemoteDesktopControl : IDisposable
{
    /// <summary>
    /// The control class the predecessor product hosted. It comes from <see cref="RdpControlHost"/> so
    /// the probe and the product can never drift onto two different control classes.
    /// </summary>
    private static readonly Guid ControlClassId = new(RdpControlHost.ClientClassId);

    /// <summary>The identifier of <c>IUnknown</c>.</summary>
    private static readonly Guid UnknownInterfaceId = new("00000000-0000-0000-C000-000000000046");

    /// <summary>The identifier of <c>IDispatch</c>, which every scriptable control must answer.</summary>
    private static readonly Guid DispatchInterfaceId = new("00020400-0000-0000-C000-000000000046");

    private const int ClassContextInprocServer = 1;

    private object? _control;
    private nint _unknown;

    private RemoteDesktopControl(object control, nint unknown)
    {
        _control = control;
        _unknown = unknown;
    }

    /// <summary>
    /// The interfaces a control may expose, paired with their identifiers. The last two are the
    /// type-library interfaces that carry the credential prompt settings.
    /// </summary>
    private static readonly (string Name, Guid Id)[] InterfacesToProbe =
    [
        ("IMsRdpExtendedSettings", new Guid("302d8188-0052-4807-806a-362b628f9ac5")),
        ("IMsRdpClientNonScriptable3", new Guid("b3378d90-0728-45c7-8ed7-b06a63b9a6f3")),
        ("IMsRdpClientNonScriptable5", new Guid("4f6996d5-f0eb-4128-bbcc-36b7e7df1e00")),
        ("IMsRdpClientNonScriptable3WithPrompt", new Guid("f50d5ce7-b9ed-4ad9-8d59-26d7d1b7b5f6")),
    ];

    /// <summary>The interfaces this experiment asks the control about.</summary>
    internal static ReadOnlySpan<(string Name, Guid Id)> Interfaces => InterfacesToProbe;

    /// <summary>Creates the control through the raw IUnknown route.</summary>
    internal static RemoteDesktopControl CreateRaw()
    {
        Guid classId = ControlClassId;
        Guid interfaceId = UnknownInterfaceId;
        int error = CoCreateInstance(in classId, 0, ClassContextInprocServer, in interfaceId, out nint unknown);
        if (error != 0)
        {
            throw new InvalidCastException(
                $"CoCreateInstance for the remote desktop control (class {classId}) failed with HRESULT 0x{error:X8}.");
        }

        object instance = Marshal.GetObjectForIUnknown(unknown);
        return new RemoteDesktopControl(instance, unknown);
    }

    /// <summary>
    /// Creates the control through the type-library route, the one the predecessor product used. This
    /// is the route through which the control answers for the interfaces its type library declares.
    /// </summary>
    internal static RemoteDesktopControl CreateThroughTypeLibrary()
    {
        Type? controlType = Type.GetTypeFromCLSID(ControlClassId);
        if (controlType is null)
        {
            throw new InvalidCastException(
                $"The remote desktop control class {ControlClassId} is not registered on this machine.");
        }

        object instance = Activator.CreateInstance(controlType)
            ?? throw new InvalidCastException($"The remote desktop control class {ControlClassId} could not be instantiated.");
        return new RemoteDesktopControl(instance, 0);
    }

    /// <summary>Describes the control without any machine-specific data.</summary>
    internal string DescribeHost()
    {
        object control = RequireControl();
        return $"class={ControlClassId} type={control.GetType().Name}";
    }

    /// <summary>Tells whether the control answers QueryInterface for one interface identifier.</summary>
    internal bool ProbeInterface(Guid interfaceId)
    {
        nint source = RequireUnknown();
        int error = Marshal.QueryInterface(source, in interfaceId, out nint probe);
        if (error != 0)
        {
            return false;
        }

        _ = Marshal.Release(probe);
        return true;
    }

    /// <summary>
    /// Tells whether the control answers QueryInterface for <c>IUnknown</c> itself. Every object must
    /// answer that, so a false here means the reference is not a usable object and every later probe
    /// would fail for that reason alone.
    /// </summary>
    internal bool AnswersIdentityQuery()
    {
        return ProbeInterface(UnknownInterfaceId);
    }

    /// <summary>Tells whether the control answers QueryInterface for <c>IDispatch</c>.</summary>
    internal bool AnswersDispatchQuery()
    {
        return ProbeInterface(DispatchInterfaceId);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_control is not null && Marshal.IsComObject(_control))
        {
            _ = Marshal.FinalReleaseComObject(_control);
        }

        _control = null;
        if (_unknown != 0)
        {
            _ = Marshal.Release(_unknown);
            _unknown = 0;
        }
    }

    private object RequireControl()
    {
        return _control ?? throw new ObjectDisposedException(nameof(RemoteDesktopControl));
    }

    /// <summary>Returns a pointer the interface questions can be asked through.</summary>
    private nint RequireUnknown()
    {
        if (_unknown != 0)
        {
            return _unknown;
        }

        nint created = Marshal.GetIUnknownForObject(RequireControl());
        _unknown = created;
        return created;
    }

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(
        in Guid classId,
        nint outer,
        int classContext,
        in Guid interfaceId,
        out nint instance);
}
