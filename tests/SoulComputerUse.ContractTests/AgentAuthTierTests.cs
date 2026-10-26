using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.SessionAgent;
using SoulComputerUse.Windows.Identity;
namespace SoulComputerUse.ContractTests;

/// <summary>
/// The credential gate tier the child-session agent detects for itself, per section 7.2: Hello first,
/// this session's own interactive logon second, refusal last. The decision table is asserted without a
/// machine, and every probe is a counting delegate so the short circuit is proved rather than assumed.
/// </summary>
[TestClass]
public sealed class AgentAuthTierTests
{
    [TestMethod]
    public void AnEnrolledHelloAlwaysDecidesWithAStrongGateAndNoHint()
    {
        foreach (bool interactiveLogon in new[] { true, false })
        {
            foreach (bool helloHardware in new[] { true, false })
            {
                AgentAuthTier detected = AgentAuthTierDetector.Decide(true, interactiveLogon, helloHardware);

                Assert.AreEqual(
                    AuthTier.HelloEnrolled,
                    detected.Tier,
                    "Hello is decided first, so it wins over every other fact.");
                Assert.AreEqual(GateStrength.Strong, detected.Strength);
                Assert.IsNull(
                    detected.HelloHint,
                    "An enrolled Hello needs no operator hint, whatever hardware or session state exists.");
                Assert.IsFalse(detected.RefusesStartup);
            }
        }
    }

    [TestMethod]
    public void WithoutHelloAnInteractiveLogonSelectsTheSystemCredentialTier()
    {
        AgentAuthTier detected = AgentAuthTierDetector.Decide(false, true, false);

        Assert.AreEqual(AuthTier.SystemCredential, detected.Tier);
        Assert.AreEqual(GateStrength.Login, detected.Strength);
        Assert.IsNull(
            detected.HelloHint,
            "A machine without Hello hardware must not hint at an enrolment that could never happen.");
        Assert.IsFalse(detected.RefusesStartup);
    }

    [TestMethod]
    public void HelloHardwareWithoutEnrolmentHintsOnlyInTheSystemCredentialTier()
    {
        AgentAuthTier detected = AgentAuthTierDetector.Decide(false, true, true);

        Assert.AreEqual(AuthTier.SystemCredential, detected.Tier);
        Assert.AreEqual(GateStrength.Login, detected.Strength);
        Assert.AreEqual(
            "helloNotEnrolled",
            detected.HelloHint,
            "Hardware that exists but is not enrolled is the one case the contract hints about.");

        Assert.IsNull(
            AgentAuthTierDetector.Decide(true, true, true).HelloHint,
            "An enrolled Hello must not hint at a missing enrolment it does not have.");
        Assert.IsNull(
            AgentAuthTierDetector.Decide(false, false, true).HelloHint,
            "The refusal tier never starts, so it never reports an operator hint.");
    }

    [TestMethod]
    public void WithoutHelloAndWithoutAnInteractiveLogonStartupIsRefused()
    {
        foreach (bool helloHardware in new[] { true, false })
        {
            AgentAuthTier detected = AgentAuthTierDetector.Decide(false, false, helloHardware);

            Assert.AreEqual(AuthTier.None, detected.Tier);
            Assert.IsTrue(detected.RefusesStartup);
        }
    }

    [TestMethod]
    public void TheRefusalTierCarriesNoGateStrength()
    {
        AgentAuthTier refused = AgentAuthTierDetector.Decide(false, false, false);

        Assert.IsNull(
            refused.Strength,
            "The refusal tier never starts a session, so the type has no strength to report for it.");
        Assert.ThrowsExactly<InvalidOperationException>(
            () => refused.RequireStrength(),
            "A caller must not be able to read a strength out of the tier that has none.");
    }

    [TestMethod]
    public void EveryStartingTierCarriesTheStrengthTheContractPins()
    {
        AgentAuthTier hello = AgentAuthTierDetector.Decide(true, false, false);
        AgentAuthTier login = AgentAuthTierDetector.Decide(false, true, false);

        Assert.AreEqual(AuthTierRules.StrengthFor(hello.Tier), hello.RequireStrength());
        Assert.AreEqual(AuthTierRules.StrengthFor(login.Tier), login.RequireStrength());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => AuthTierRules.StrengthFor(AgentAuthTierDetector.Decide(false, false, false).Tier));
    }

    [TestMethod]
    public void TheTypeRejectsAStrengthOrHintThatContradictsItsTier()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new AgentAuthTier(AuthTier.None, GateStrength.Login, null).Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new AgentAuthTier(AuthTier.HelloEnrolled, GateStrength.Login, null).Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new AgentAuthTier(AuthTier.SystemCredential, GateStrength.Strong, null).Validate());
        Assert.ThrowsExactly<InvalidOperationException>(
            () => new AgentAuthTier(AuthTier.HelloEnrolled, GateStrength.Strong, "helloNotEnrolled").Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new AgentAuthTier(AuthTier.SystemCredential, GateStrength.Login, "enrol hello first").Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new AgentAuthTier((AuthTier)99, GateStrength.Strong, null).Validate());
    }

    [TestMethod]
    public void AnEnrolledHelloNeverReadsTheLogonOrHardwareProbe()
    {
        Counter hello = new(true);
        Counter hardware = new(true);
        Counter interactiveLogon = new(false);

        AgentAuthTier detected = AgentAuthTierDetector.Detect(hello.Probe, hardware.Probe, interactiveLogon.Probe);

        Assert.AreEqual(AuthTier.HelloEnrolled, detected.Tier);
        Assert.AreEqual(1, hello.Calls, "The Hello verdict is the first fact, so it is read exactly once.");
        Assert.AreEqual(0, interactiveLogon.Calls, "An enrolled Hello decides, so the logon fact is never read.");
        Assert.AreEqual(0, hardware.Calls, "An enrolled Hello decides, so the hardware hint is never read.");
    }

    [TestMethod]
    public void ARefusedSessionNeverReadsTheHardwareProbe()
    {
        Counter hello = new(false);
        Counter hardware = new(true);
        Counter interactiveLogon = new(false);

        AgentAuthTier detected = AgentAuthTierDetector.Detect(hello.Probe, hardware.Probe, interactiveLogon.Probe);

        Assert.AreEqual(AuthTier.None, detected.Tier);
        Assert.AreEqual(1, hello.Calls);
        Assert.AreEqual(1, interactiveLogon.Calls, "The logon fact is the second step, so it is read once.");
        Assert.AreEqual(0, hardware.Calls, "The refusal tier reports no hint, so it must not read the hardware fact.");
    }

    [TestMethod]
    public void TheHardwareProbeIsReadOnlyWhenTheHintCanBeReported()
    {
        Counter hello = new(false);
        Counter hardware = new(true);
        Counter interactiveLogon = new(true);

        AgentAuthTier detected = AgentAuthTierDetector.Detect(hello.Probe, hardware.Probe, interactiveLogon.Probe);

        Assert.AreEqual(AuthTier.SystemCredential, detected.Tier);
        Assert.AreEqual("helloNotEnrolled", detected.HelloHint);
        Assert.AreEqual(1, hello.Calls);
        Assert.AreEqual(1, interactiveLogon.Calls);
        Assert.AreEqual(1, hardware.Calls, "The hint is reported in this branch, so the hardware fact is read exactly once.");
    }

    [TestMethod]
    public void AFailingProbeIsAnExceptionAndNeverATier()
    {
        InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(
            () => AgentAuthTierDetector.DetectFailClosed(
                ThrowingProbe,
                () => false,
                () => true));

        Assert.IsInstanceOfType<ProbeFailureException>(
            error.InnerException,
            "The original probe failure must be kept, so the cause is not hidden.");
        StringAssert.Contains(
            error.Message,
            "could not be detected",
            "The failure must state that no tier was detected instead of presenting one.");
    }

    [TestMethod]
    public void AFailingLogonProbeIsAnExceptionAndNeverARefusalTier()
    {
        Assert.ThrowsExactly<InvalidOperationException>(
            () => AgentAuthTierDetector.DetectFailClosed(
                () => false,
                () => false,
                ThrowingProbe),
            "An unreadable logon fact must not be read as 'no interactive logon', which would look like a definite refusal.");
    }

    [TestMethod]
    public void AWorkingDetectionPassesThroughFailClosedUnchanged()
    {
        AgentAuthTier detected = AgentAuthTierDetector.DetectFailClosed(
            () => false,
            () => true,
            () => true);

        Assert.AreEqual(AuthTier.SystemCredential, detected.Tier);
        Assert.AreEqual("helloNotEnrolled", detected.HelloHint);
    }

    [TestMethod]
    public void DetectRefusesAMissingProbe()
    {
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.Detect(null!, () => false, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.Detect(() => false, null!, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.Detect(() => false, () => false, null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.Detect(null!, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.Detect(() => false, null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.DetectFailClosed(null!, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.DetectFailClosed(() => false, null!));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.DetectFailClosed(null!, () => false, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.DetectFailClosed(() => false, null!, () => false));
        Assert.ThrowsExactly<ArgumentNullException>(
            () => AgentAuthTierDetector.DetectFailClosed(() => false, () => false, null!));
    }

    [TestMethod]
    public void TheUnavailableHelloFactsNeverClaimAnEnrolledHelloOrAnEnrolmentHint()
    {
        AgentHelloFacts facts = AgentHelloFacts.Unavailable;

        Assert.IsFalse(
            facts.AvailableAndEnrolled(),
            "The program cannot query Hello, so it must report absence instead of assuming the strong gate.");
        Assert.IsFalse(
            facts.HardwarePresentButNotEnrolled(),
            "An unqueried hardware fact must not invent an enrolment hint.");
    }

    [TestMethod]
    public void TheTwoProbeWrapperNeverReachesTheHardwareProbeWhenHelloDecides()
    {
        Counter hardware = new(true);

        AgentAuthTier detected = AgentAuthTierDetector.Detect(() => true, hardware.Probe);

        Assert.AreEqual(AuthTier.HelloEnrolled, detected.Tier);
        Assert.AreEqual(GateStrength.Strong, detected.Strength);
        Assert.AreEqual(
            0,
            hardware.Calls,
            "The wrapper must read the hardware fact only after Hello failed, so an enrolled Hello short-circuits it.");
    }

    [TestMethod]
    public void TheTwoProbeWrapperReadsTheHardwareFactOnlyWhereTheHintCouldBeReported()
    {
        Counter hardware = new(true);
        bool realInteractiveLogon = InteractiveLogonProbe.HasInteractiveLogonToken();

        AgentAuthTier detected = AgentAuthTierDetector.Detect(() => false, hardware.Probe);

        Assert.AreEqual(
            AuthTierProbe.Decide(false, realInteractiveLogon),
            detected.Tier,
            "The wrapper must use the real session logon fact rather than a value of its own.");
        Assert.AreEqual(
            realInteractiveLogon ? 1 : 0,
            hardware.Calls,
            "The hardware fact is read exactly on the branch where the operator hint can be reported.");
        Assert.AreEqual(
            realInteractiveLogon ? AgentAuthTier.HelloNotEnrolledHint : null,
            detected.HelloHint,
            "The hint appears only in the system-credential tier, which is the only tier that can carry it.");
    }

    [TestMethod]
    public void TheTwoProbeOverloadShortCircuitsBeforeTheHardwareProbe()
    {
        AgentAuthTier detected = AgentAuthTierDetector.Detect(() => true, ThrowingProbe);

        Assert.AreEqual(
            AuthTier.HelloEnrolled,
            detected.Tier,
            "An enrolled Hello decides before the hardware hint is read, so the probe that would fail is never called.");
        Assert.AreEqual(GateStrength.Strong, detected.Strength);
        Assert.IsNull(detected.HelloHint);
    }

    [TestMethod]
    public void TheRealSessionProbeAgreesWithTheWindowsTierProbe()
    {
        AgentAuthTier detected = AgentAuthTierDetector.Detect(() => false, () => false);

        Assert.AreEqual(
            AuthTierProbe.Decide(false, InteractiveLogonProbe.HasInteractiveLogonToken()),
            detected.Tier,
            "The agent must read the same session fact through the same rule as the startup probe, or the two layers could disagree.");
    }

    private static bool ThrowingProbe()
    {
        throw new ProbeFailureException("The simulated system probe failed.");
    }

    /// <summary>A probe that counts how often the detector reads it.</summary>
    private sealed class Counter
    {
        private readonly bool _value;

        internal Counter(bool value)
        {
            _value = value;
        }

        internal int Calls { get; private set; }

        internal bool Probe()
        {
            Calls++;
            return _value;
        }
    }

    /// <summary>Stands in for a system probe that cannot read its fact.</summary>
    private sealed class ProbeFailureException : Exception
    {
        internal ProbeFailureException(string message)
            : base(message)
        {
        }
    }
}