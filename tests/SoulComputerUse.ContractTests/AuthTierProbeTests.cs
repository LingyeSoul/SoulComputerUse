using System;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Identity;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// The credential gate probe of section 7.2: Hello first, this session's own interactive logon
/// second, refusal last. The decision table is asserted without a machine; the injected Hello seam
/// is asserted against the real session fact without pinning what that fact is.
/// </summary>
[TestClass]
public sealed class AuthTierProbeTests
{
    [TestMethod]
    public void HelloEnrolledWinsOverEverySessionLogonFact()
    {
        Assert.AreEqual(
            AuthTier.HelloEnrolled,
            AuthTierProbe.Decide(true, true),
            "Hello is checked first, so an enrolled Hello decides even when the session is interactive.");

        Assert.AreEqual(
            AuthTier.HelloEnrolled,
            AuthTierProbe.Decide(true, false));
    }

    [TestMethod]
    public void WithoutHelloAnInteractiveLogonSelectsTheSystemCredentialTier()
    {
        Assert.AreEqual(
            AuthTier.SystemCredential,
            AuthTierProbe.Decide(false, true),
            "An active signed-in session is the child session's own Windows logon gate.");
    }

    [TestMethod]
    public void WithoutHelloAndWithoutAnInteractiveLogonStartupIsRefused()
    {
        Assert.AreEqual(
            AuthTier.None,
            AuthTierProbe.Decide(false, false),
            "Only a definite absence of both gate facts may produce the refusal tier.");
    }

    [TestMethod]
    public void EveryDecisionMatchesTheGateStrengthRules()
    {
        Assert.AreEqual(GateStrength.Strong, AuthTierRules.StrengthFor(AuthTierProbe.Decide(true, true)));
        Assert.AreEqual(GateStrength.Strong, AuthTierRules.StrengthFor(AuthTierProbe.Decide(true, false)));
        Assert.AreEqual(GateStrength.Login, AuthTierRules.StrengthFor(AuthTierProbe.Decide(false, true)));

        Assert.ThrowsExactly<InvalidOperationException>(
            () => AuthTierRules.StrengthFor(AuthTierProbe.Decide(false, false)),
            "The refusal tier never starts, so it must not claim a gate strength.");
    }

    [TestMethod]
    public void DetectUsesTheInjectedHelloAnswerAsIs()
    {
        Assert.AreEqual(
            AuthTier.HelloEnrolled,
            AuthTierProbe.Detect(() => true),
            "An enrolled Hello must decide the tier before the session probe runs.");

        AuthTier tier = AuthTierProbe.Detect(() => false);

        Assert.AreNotEqual(
            AuthTier.HelloEnrolled,
            tier,
            "A 'not enrolled' answer must not be replaced by an assumed Hello.");

        Assert.AreEqual(
            AuthTierProbe.Decide(false, InteractiveLogonProbe.HasInteractiveLogonToken()),
            tier,
            "A 'not enrolled' answer must be passed through unchanged and combined with this session's own logon fact.");
    }

    [TestMethod]
    public void DetectMachineUsesTheSameInjectedHelloAnswer()
    {
        Assert.AreEqual(
            AuthTier.HelloEnrolled,
            AuthTierProbe.DetectMachine(() => true),
            "The production name must go through the same decision table as Detect.");
    }

    [TestMethod]
    public void DetectRefusesAMissingHelloProbe()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(
            () => AuthTierProbe.Detect(null!));

        Assert.AreEqual("helloAvailableAndEnrolled", error.ParamName);
    }

    [TestMethod]
    public void DetectMachineRefusesAMissingHelloProbe()
    {
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(
            () => AuthTierProbe.DetectMachine(null!));

        Assert.AreEqual("helloAvailableAndEnrolled", error.ParamName);
    }

    [TestMethod]
    public void TheRealSessionProbeYieldsAContractTierWithoutThrowing()
    {
        AuthTier tier = AuthTierProbe.Detect(() => false);

        Assert.IsTrue(
            Enum.IsDefined(tier),
            "The real session probe must land on one of the three contract tiers, never on an unknown value.");
    }
}
