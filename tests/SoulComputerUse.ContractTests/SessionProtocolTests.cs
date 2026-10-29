using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Windows.Ipc;
using SoulComputerUse.Windows.Ipc.Security;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the shared tool surface used by the CLI and the MCP adapter: the wire names,
/// the published JSON schemas, the argument rules and the status payload.
/// </summary>
[TestClass]
public sealed class SessionProtocolTests
{
    [TestMethod]
    public void EveryToolHasTheContractsWireNameAndRoundTrips()
    {
        Dictionary<SessionTool, string> expected = new()
        {
            [SessionTool.GetStatus] = "get_status",
            [SessionTool.Screenshot] = "screenshot",
            [SessionTool.MouseMove] = "mouse_move",
            [SessionTool.MouseClick] = "mouse_click",
            [SessionTool.MouseScroll] = "mouse_scroll",
            [SessionTool.KeyType] = "key_type",
            [SessionTool.KeyPress] = "key_press",
        };

        Assert.HasCount(7, expected, "The contract defines exactly seven tools.");
        Assert.HasCount(expected.Count, SessionToolCatalog.Tools, "The catalog must expose every tool.");

        foreach ((SessionTool tool, string wireName) in expected)
        {
            Assert.AreEqual(wireName, SessionToolNames.ToWireName(tool));
            Assert.AreEqual(tool, SessionToolNames.FromWireName(wireName));
            Assert.IsTrue(SessionToolCatalog.Tools.Contains(tool), "The catalog is missing a tool.");
        }

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolNames.FromWireName("shell"));
    }

    [TestMethod]
    public void EveryPublishedSchemaIsValidJsonWithAnObjectRoot()
    {
        foreach (SessionTool tool in SessionToolCatalog.Tools)
        {
            string schema = SessionToolCatalog.SchemaOf(tool);
            using JsonDocument document = JsonDocument.Parse(schema);

            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind, $"{tool} must publish an object schema.");
            Assert.AreEqual("object", document.RootElement.GetProperty("type").GetString());
            Assert.IsGreaterThan(0, SessionToolCatalog.Describe(tool).Length, $"{tool} needs a description.");
        }
    }

    [TestMethod]
    public void TheScreenshotLimitAlwaysFitsInsideOneOwnerFrame()
    {
        // Base64 turns every three bytes into four characters, so a contract-sized PNG must still
        // leave room for the JSON envelope inside one frame; otherwise a valid screenshot would be
        // refused by the pipe before the client ever sees it.
        int encodedBytes = ((SessionIpcRules.ScreenshotByteLimit + 2) / 3) * 4;
        const int envelopeHeadroom = 64 * 1024;

        Assert.IsLessThan(
            OwnerPipeLimits.MaximumFrameBytes,
            encodedBytes + envelopeHeadroom,
            $"A {SessionIpcRules.ScreenshotByteLimit}-byte PNG becomes {encodedBytes} base64 bytes, which does not fit inside the {OwnerPipeLimits.MaximumFrameBytes}-byte owner frame limit.");
    }

    [TestMethod]
    public void CoordinateToolsRequireBothCoordinatesAndForbidExtras()
    {
        foreach (SessionTool tool in (SessionTool[])[SessionTool.MouseMove, SessionTool.MouseClick, SessionTool.MouseScroll])
        {
            using JsonDocument document = JsonDocument.Parse(SessionToolCatalog.SchemaOf(tool));
            JsonElement root = document.RootElement;

            Assert.IsFalse(root.GetProperty("additionalProperties").GetBoolean(), $"{tool} must forbid unknown arguments.");
            Assert.IsTrue(root.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "x"));
            Assert.IsTrue(root.GetProperty("required").EnumerateArray().Any(value => value.GetString() == "y"));
        }
    }

    [TestMethod]
    public void ClickAndKeySchemasCarryTheContractBounds()
    {
        using JsonDocument click = JsonDocument.Parse(SessionToolCatalog.SchemaOf(SessionTool.MouseClick));
        JsonElement count = click.RootElement.GetProperty("properties").GetProperty("count");
        Assert.AreEqual(1, count.GetProperty("minimum").GetInt32());
        Assert.AreEqual(3, count.GetProperty("maximum").GetInt32());

        using JsonDocument keys = JsonDocument.Parse(SessionToolCatalog.SchemaOf(SessionTool.KeyPress));
        JsonElement keyArray = keys.RootElement.GetProperty("properties").GetProperty("keys");
        Assert.AreEqual(1, keyArray.GetProperty("minItems").GetInt32());
        Assert.AreEqual(5, keyArray.GetProperty("maxItems").GetInt32());
        Assert.HasCount(15, keyArray.GetProperty("items").GetProperty("enum").EnumerateArray().ToArray());

        using JsonDocument text = JsonDocument.Parse(SessionToolCatalog.SchemaOf(SessionTool.KeyType));
        Assert.AreEqual(1000, text.RootElement.GetProperty("properties").GetProperty("text").GetProperty("maxLength").GetInt32());
    }

    [TestMethod]
    public void EmptyArgumentToolsTakeNoArguments()
    {
        foreach (SessionTool tool in (SessionTool[])[SessionTool.GetStatus, SessionTool.Screenshot])
        {
            using JsonDocument document = JsonDocument.Parse(SessionToolCatalog.SchemaOf(tool));
            Assert.IsFalse(document.RootElement.GetProperty("additionalProperties").GetBoolean());
            Assert.AreEqual(0, document.RootElement.GetProperty("required").GetArrayLength());
            Assert.AreEqual(0, document.RootElement.GetProperty("properties").EnumerateObject().Count());
        }
    }

    [TestMethod]
    public void CoordinateValidationUsesTheRemoteScreenBounds()
    {
        SessionScreen screen = new(1920, 1080);

        SessionToolArguments.RequireCoordinate(0, 0, screen);
        SessionToolArguments.RequireCoordinate(1919, 1079, screen);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireCoordinate(1920, 0, screen));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireCoordinate(0, 1080, screen));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireCoordinate(-1, 0, screen));
    }

    [TestMethod]
    public void ClickCountTextAndKeysUseTheContractLimits()
    {
        SessionToolArguments.RequireClickCount(1);
        SessionToolArguments.RequireClickCount(3);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireClickCount(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireClickCount(4));

        SessionToolArguments.RequireText(string.Empty);
        SessionToolArguments.RequireText(new string('a', 1000));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireText(new string('a', 1001)));

        SessionToolArguments.RequireKeys([SessionKey.Ctrl]);
        SessionToolArguments.RequireKeys([SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete, SessionKey.Shift, SessionKey.Enter]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolArguments.RequireKeys([]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => SessionToolArguments.RequireKeys([SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete, SessionKey.Shift, SessionKey.Enter, SessionKey.Tab]));
    }

    [TestMethod]
    public void WireNamesForButtonsAndKeysRoundTrip()
    {
        foreach (MouseButton button in Enum.GetValues<MouseButton>())
        {
            Assert.AreEqual(button, MouseButtonNames.FromWireName(MouseButtonNames.ToWireName(button)));
        }

        foreach (SessionKey key in Enum.GetValues<SessionKey>())
        {
            Assert.AreEqual(key, SessionKeyNames.FromWireName(SessionKeyNames.ToWireName(key)));
        }

        Assert.HasCount(3, Enum.GetValues<MouseButton>());
        Assert.HasCount(15, Enum.GetValues<SessionKey>());
    }

    [TestMethod]
    public void StatusValidationRefusesTheNoneTierAndUnknownHelloHints()
    {
        SessionStatus valid = new("ready", "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f", AuthTier.SystemCredential, GateStrength.Login, new SessionScreen(1920, 1080), "helloNotEnrolled");
        valid.Validate();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStatus("ready", "id", AuthTier.None, GateStrength.Login, new SessionScreen(1, 1), null).Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStatus("ready", "id", AuthTier.SystemCredential, GateStrength.Login, new SessionScreen(1, 1), "pin").Validate());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new SessionStatus("ready", "id", AuthTier.SystemCredential, GateStrength.Login, new SessionScreen(0, 1), null).Validate());
    }

    [TestMethod]
    public async Task TheUnavailableClientFailsClosedWithTheDisconnectedCode()
    {
        UnavailableSessionClient client = new();

        SessionOperationException error = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => client.GetStatusAsync(CancellationToken.None));

        Assert.AreEqual(SessionErrorCode.Disconnected, error.Code);
        Assert.IsFalse(error.Retryable);

        SessionOperationException screenshot = await Assert.ThrowsExactlyAsync<SessionOperationException>(
            () => client.ScreenshotAsync(CancellationToken.None));
        Assert.AreEqual(SessionErrorCode.Disconnected, screenshot.Code);
    }
}
