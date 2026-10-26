using System;
using System.Windows.Forms;
using SoulComputerUse.Windows.Rdp;

namespace SoulComputerUse.RdpCredentialExperiment;

/// <summary>
/// The probe window: one form hosting the remote desktop control, closed by the operator or after the
/// observation window expires.
/// </summary>
internal sealed class ProbeWindow : Form
{
    private readonly RdpControlHost _control;

    internal ProbeWindow(int width, int height)
    {
        Text = "SoulComputerUse RDP 连接实验";
        ClientSize = new System.Drawing.Size(width, height);
        StartPosition = FormStartPosition.CenterScreen;

        _control = new RdpControlHost
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_control);
    }

    /// <summary>The raw control object, available once the handle exists.</summary>
    internal object? Ocx => _control.Ocx;

    /// <inheritdoc />
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _ = _control.Handle;
    }
}
