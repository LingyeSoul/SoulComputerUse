using System;
using SoulComputerUse.Windows.ChildSessions;

namespace SoulComputerUse.IntegrationTests;

/// <summary>
/// The child-session switch lifecycle. The decision table is checked over all eighteen input
/// combinations, every refusal is required to name the policy value that has to change, the raw
/// policy value parser is checked for the missing and wrong-type branches, and the real registry
/// read is exercised against this machine.
/// </summary>
[TestClass]
public sealed class ChildSessionsLifecycleTests
{
    [TestMethod]
    public void ExplicitlyDeniedRdpInboundRefusesAndNamesTheValue()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: true,
            rdpInbound: PolicyState.Denied,
            childConnections: PolicyState.Allowed);

        Assert.AreEqual(ChildSessionsDecisionKind.Refuse, decision.Kind);
        Assert.IsFalse(decision.AllowsConnection, "A refused decision must never allow a connection.");
        Assert.AreEqual(PolicyValue.RdpInbound, decision.Value);
        Assert.AreEqual(PolicyProblem.Denied, decision.Problem);
        Assert.IsNotNull(decision.Message);
        Assert.IsTrue(
            decision.Message.Contains(ChildSessionsPolicy.RdpInboundValueName, StringComparison.Ordinal),
            "The refusal must name the exact policy value the operator has to change.");
        Assert.IsTrue(
            decision.Message.Contains("DWORD 0", StringComparison.Ordinal),
            "The refusal must say what value allows the session.");
    }

    [TestMethod]
    public void MissingRdpInboundValueRefusesAsMissing()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: true,
            rdpInbound: PolicyState.Missing,
            childConnections: PolicyState.Allowed);

        Assert.AreEqual(ChildSessionsDecisionKind.Refuse, decision.Kind);
        Assert.AreEqual(PolicyValue.RdpInbound, decision.Value);
        Assert.AreEqual(
            PolicyProblem.Missing,
            decision.Problem,
            "An absent value must be reported as missing, not as an explicit denial: the operator fixes them differently.");
        Assert.IsNotNull(decision.Message);
        Assert.IsTrue(decision.Message.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void MissingChildConnectionValueRefusesAndNamesTheValue()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: true,
            rdpInbound: PolicyState.Allowed,
            childConnections: PolicyState.Missing);

        Assert.AreEqual(ChildSessionsDecisionKind.Refuse, decision.Kind);
        Assert.AreEqual(PolicyValue.ChildConnections, decision.Value);
        Assert.AreEqual(PolicyProblem.Missing, decision.Problem);
        Assert.IsNotNull(decision.Message);
        Assert.IsTrue(
            decision.Message.Contains(ChildSessionsPolicy.ChildConnectionsValueName, StringComparison.Ordinal),
            "The refusal must name the exact policy value the operator has to change.");
    }

    [TestMethod]
    public void RdpInboundIsReportedBeforeChildConnections()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: true,
            rdpInbound: PolicyState.Denied,
            childConnections: PolicyState.Denied);

        Assert.AreEqual(PolicyValue.RdpInbound, decision.Value, "When both policies refuse, the RDP inbound value is the one reported.");
    }

    [TestMethod]
    public void BothPreconditionsAllowedWithTheSwitchOnProceeds()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: true,
            rdpInbound: PolicyState.Allowed,
            childConnections: PolicyState.Allowed);

        Assert.AreEqual(ChildSessionsDecisionKind.Proceed, decision.Kind);
        Assert.IsNull(decision.Value, "A decision that does not refuse carries no policy value.");
        Assert.IsTrue(decision.AllowsConnection);
    }

    [TestMethod]
    public void BothPreconditionsAllowedWithTheSwitchOffRequestsElevation()
    {
        ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(
            childSessionsEnabled: false,
            rdpInbound: PolicyState.Allowed,
            childConnections: PolicyState.Allowed);

        Assert.AreEqual(ChildSessionsDecisionKind.RequestElevation, decision.Kind);
        Assert.IsTrue(decision.AllowsConnection, "Requesting elevation is not a refusal.");
        Assert.IsNotNull(decision.Message);
    }

    [TestMethod]
    public void DecisionTableCoversAllEighteenCombinations()
    {
        int combinations = 0;
        foreach (bool enabled in new[] { false, true })
        {
            foreach (PolicyState rdpInbound in Enum.GetValues<PolicyState>())
            {
                foreach (PolicyState childConnections in Enum.GetValues<PolicyState>())
                {
                    ChildSessionsDecision decision = ChildSessionsPolicy.Evaluate(enabled, rdpInbound, childConnections);

                    if (rdpInbound != PolicyState.Allowed)
                    {
                        AssertRefusal(decision, PolicyValue.RdpInbound, rdpInbound);
                    }
                    else if (childConnections != PolicyState.Allowed)
                    {
                        AssertRefusal(decision, PolicyValue.ChildConnections, childConnections);
                    }
                    else
                    {
                        ChildSessionsDecisionKind expected = enabled
                            ? ChildSessionsDecisionKind.Proceed
                            : ChildSessionsDecisionKind.RequestElevation;
                        Assert.AreEqual(expected, decision.Kind, $"enabled={enabled} must decide between proceed and elevation.");
                    }

                    combinations++;
                }
            }
        }

        Assert.AreEqual(18, combinations, "The three-state decision table must be covered exhaustively.");
    }

    [TestMethod]
    public void MissingPolicyValueIsNotConsent()
    {
        Assert.AreEqual(PolicyState.Missing, ChildSessionsProbe.InterpretPolicyValue("example-value", null));
    }

    [TestMethod]
    public void ZeroPolicyValueAllowsAndNonZeroDenies()
    {
        Assert.AreEqual(PolicyState.Allowed, ChildSessionsProbe.InterpretPolicyValue("example-value", 0));
        Assert.AreEqual(PolicyState.Denied, ChildSessionsProbe.InterpretPolicyValue("example-value", 1));
        Assert.AreEqual(PolicyState.Denied, ChildSessionsProbe.InterpretPolicyValue("example-value", -1));
    }

    [TestMethod]
    public void WrongTypedPolicyValueIsAControlledErrorNotACrash()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ChildSessionsProbe.InterpretPolicyValue("example-value", "0"));
        Assert.ThrowsExactly<InvalidOperationException>(
            () => ChildSessionsProbe.InterpretPolicyValue("example-value", 0L));
    }

    [TestMethod]
    public void RealHardPreconditionsAreReadableOnThisMachine()
    {
        ChildSessionHardPreconditions preconditions = ChildSessionsProbe.ReadHardPreconditions();

        Assert.AreNotEqual(
            PolicyState.Missing,
            preconditions.RdpInbound,
            "This machine is expected to define the RDP inbound policy value; the read path must reach it.");
        Assert.AreNotEqual(
            PolicyState.Missing,
            preconditions.ChildConnections,
            "This machine is expected to define the child-connection policy value; the read path must reach it.");
    }

    [TestMethod]
    public void RealChildSessionProbeIsStableWithinOneRun()
    {
        bool first = ChildSessionsProbe.IsEnabled();
        bool second = ChildSessionsProbe.IsEnabled();

        Assert.AreEqual(first, second, "WTSIsChildSessionsEnabled must return a stable answer while the machine state does not change.");
    }

    private static void AssertRefusal(ChildSessionsDecision decision, PolicyValue expectedValue, PolicyState state)
    {
        Assert.AreEqual(ChildSessionsDecisionKind.Refuse, decision.Kind);
        Assert.IsFalse(decision.AllowsConnection);
        Assert.AreEqual(expectedValue, decision.Value);
        Assert.AreEqual(
            state == PolicyState.Missing ? PolicyProblem.Missing : PolicyProblem.Denied,
            decision.Problem);
        Assert.IsNotNull(decision.Message);
    }
}