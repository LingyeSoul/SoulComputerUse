using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// Adversarial verification of the child-session hard preconditions. The missing-value and
/// non-DWORD branches are proven against real registry data in a throwaway per-test key under the
/// current user: the machine-wide HKLM policy is never written, so no system setting is touched.
/// </summary>
[TestClass]
public sealed class ChildSessionsProbeAdversarialTests
{
    private const string ScratchKeyRoot = @"Software\SoulComputerUse-Adversarial";

    private string _scratchKeyPath = string.Empty;

    [TestInitialize]
    public void CreateScratchKey()
    {
        _scratchKeyPath = ScratchKeyRoot + @"\" + Guid.NewGuid().ToString("N");
        using RegistryKey? created = Registry.CurrentUser.CreateSubKey(_scratchKeyPath);
        Assert.IsNotNull(created, "The throwaway registry key could not be created, so this test cannot mean anything.");
    }

    [TestCleanup]
    public void DeleteScratchKey()
    {
        if (!string.IsNullOrEmpty(_scratchKeyPath))
        {
            Registry.CurrentUser.DeleteSubKeyTree(_scratchKeyPath, throwOnMissingSubKey: false);
        }
    }

    /// <summary>
    /// A value that does not exist must refuse the connection, and the refusal must name the missing
    /// policy value and say how to allow it. Proved against real registry data for both values.
    /// </summary>
    [TestMethod]
    public void MissingRegistryValueIsRefusedAndNamesTheValueToFix()
    {
        List<(string ValueName, PolicyValue Value)> cases =
        [
            (ChildSessionsPolicy.RdpInboundValueName, PolicyValue.RdpInbound),
            (ChildSessionsPolicy.ChildConnectionsValueName, PolicyValue.ChildConnections),
        ];

        foreach ((string ValueName, PolicyValue Value) expected in cases)
        {
            using RegistryKey scratch = OpenScratchKey();
            PolicyState state = ChildSessionsProbe.InterpretPolicyValue(
                expected.ValueName,
                scratch.GetValue(expected.ValueName));

            Assert.AreEqual(
                PolicyState.Missing,
                state,
                $"An absent policy value must be reported as missing, not allowed: {expected.ValueName}.");

            ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
                childSessionsEnabled: true,
                expected.Value == PolicyValue.RdpInbound ? state : PolicyState.Allowed,
                expected.Value == PolicyValue.ChildConnections ? state : PolicyState.Allowed);

            Assert.AreEqual(
                ChildSessionsDecisionKind.Refuse,
                decision.Kind,
                $"A missing value must refuse the connection even when the switch is already on: {expected.ValueName}.");
            Assert.AreEqual(expected.Value, decision.Value, "The decision must name the value that refused.");
            Assert.AreEqual(PolicyProblem.Missing, decision.Problem, "The decision must say the value is missing, not denied.");
            Assert.IsNotNull(decision.Message, "A refusal must explain itself to the operator.");
            StringAssert.Contains(
                decision.Message,
                expected.ValueName,
                "The message must name the missing policy value so the operator knows what to fix.");
            StringAssert.Contains(
                decision.Message,
                "DWORD 0",
                "The message must say that an explicit DWORD 0 is what allows the connection.");
        }
    }

    /// <summary>A string value must be refused as unusable, never silently read as "allowed".</summary>
    [TestMethod]
    public void StringRegistryValueIsRefusedInsteadOfBeingReadAsZero()
    {
        using (RegistryKey scratch = OpenScratchKey())
        {
            scratch.SetValue(ChildSessionsPolicy.RdpInboundValueName, "0", RegistryValueKind.String);
        }

        using RegistryKey written = OpenScratchKey();
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(
            () => ChildSessionsProbe.InterpretPolicyValue(
                ChildSessionsPolicy.RdpInboundValueName,
                written.GetValue(ChildSessionsPolicy.RdpInboundValueName)));

        StringAssert.Contains(
            failure.Message,
            ChildSessionsPolicy.RdpInboundValueName,
            "The failure must name the policy value so the operator can fix it.");
    }

    /// <summary>Real DWORD values must map to allowed and denied exactly as the contract says.</summary>
    [TestMethod]
    public void RealDwordRegistryValuesMapToAllowedAndDenied()
    {
        foreach ((int Raw, PolicyState Expected) caseValue in new List<(int Raw, PolicyState Expected)>
        {
            (0, PolicyState.Allowed),
            (1, PolicyState.Denied),
            (42, PolicyState.Denied),
        })
        {
            using (RegistryKey scratch = OpenScratchKey())
            {
                scratch.SetValue(ChildSessionsPolicy.ChildConnectionsValueName, caseValue.Raw, RegistryValueKind.DWord);
            }

            using RegistryKey written = OpenScratchKey();
            PolicyState state = ChildSessionsProbe.InterpretPolicyValue(
                ChildSessionsPolicy.ChildConnectionsValueName,
                written.GetValue(ChildSessionsPolicy.ChildConnectionsValueName));

            Assert.AreEqual(caseValue.Expected, state, $"A DWORD {caseValue.Raw} must map to {caseValue.Expected}.");
        }
    }

    /// <summary>Proceeding must require both policy values to allow the connection.</summary>
    [TestMethod]
    public void ProceedingRequiresBothPolicyValuesToAllowTheConnection()
    {
        foreach (PolicyState rdpInbound in Enum.GetValues<PolicyState>())
        {
            foreach (PolicyState childConnections in Enum.GetValues<PolicyState>())
            {
                ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
                    childSessionsEnabled: true,
                    rdpInbound,
                    childConnections);

                bool bothAllowed = rdpInbound == PolicyState.Allowed && childConnections == PolicyState.Allowed;
                Assert.AreEqual(
                    bothAllowed,
                    decision.Kind == ChildSessionsDecisionKind.Proceed,
                    $"Proceeding must require both values to allow the connection, but rdpInbound={rdpInbound} and childConnections={childConnections} produced {decision.Kind}.");
            }
        }
    }

    /// <summary>The real machine read must work and stay consistent with the decision table.</summary>
    [TestMethod]
    public void RealMachinePreconditionsAreReadableAndConsistentWithTheDecision()
    {
        ChildSessionHardPreconditions preconditions = ChildSessionsProbe.ReadHardPreconditions();
        bool enabled = ChildSessionsProbe.IsEnabled();

        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(enabled, preconditions.RdpInbound, preconditions.ChildConnections);

        bool bothAllowed = preconditions.RdpInbound == PolicyState.Allowed && preconditions.ChildConnections == PolicyState.Allowed;
        Assert.AreEqual(
            bothAllowed,
            decision.AllowsConnection,
            $"The real machine reported rdpInbound={preconditions.RdpInbound} and childConnections={preconditions.ChildConnections}, but the decision was {decision.Kind}.");

        if (!bothAllowed)
        {
            Assert.IsNotNull(decision.Message, "A refusal must explain itself to the operator.");
        }
    }

    private RegistryKey OpenScratchKey()
    {
        return Registry.CurrentUser.OpenSubKey(_scratchKeyPath, writable: true)
            ?? throw new InvalidOperationException(
                "The throwaway registry key vanished before it could be read, so this test cannot mean anything.");
    }
}