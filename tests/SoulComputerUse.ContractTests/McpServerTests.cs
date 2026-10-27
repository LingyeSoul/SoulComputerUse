using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SoulComputerUse.Core.Protocol;
using SoulComputerUse.Core.Session;
using SoulComputerUse.Mcp;
using SoulComputerUse.Windows.Hosting.Discovery;
using SoulComputerUse.Windows.Hosting.Launch;

namespace SoulComputerUse.ContractTests;

/// <summary>
/// Contract tests for the MCP stdio adapter: the JSON-RPC envelope, the published tool list, the
/// mapping of every tool onto the session client, and the section 5 result shapes. The suite drives
/// the real server over in-memory streams with a recording client, so it never touches a session.
/// </summary>
[TestClass]
public sealed class McpServerTests
{
    private const int ScreenshotLimit = 5_242_880;

    private static readonly string[] ScreenshotCallOrder = ["get_status", "screenshot"];

    private static readonly string[] MoveCallOrder = ["get_status", "mouse_move"];

    private static readonly string[] StatusOnlyCallOrder = ["get_status"];

    private static readonly SessionKey[] CtrlAltDelete = [SessionKey.Ctrl, SessionKey.Alt, SessionKey.Delete];

    /// <summary>The status the recording client reports unless a test overrides it.</summary>
    private static readonly SessionStatus ReadyStatus = new(
        "ready",
        "b3f1c2d4-5678-4abc-9def-0a1b2c3d4e5f",
        AuthTier.SystemCredential,
        GateStrength.Login,
        new SessionScreen(1920, 1080),
        null);

    [TestMethod]
    public async Task InitializeEchoesASupportedProtocolVersionAndAdvertisesTools()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Request("1", "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"probe","version":"1"}}""")]);

        Assert.HasCount(1, exchange.Responses);
        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement root = response.RootElement;
        Assert.AreEqual("2.0", root.GetProperty("jsonrpc").GetString());
        Assert.AreEqual(1, root.GetProperty("id").GetInt32());

        JsonElement result = root.GetProperty("result");
        Assert.AreEqual("2025-06-18", result.GetProperty("protocolVersion").GetString());
        Assert.AreEqual(JsonValueKind.Object, result.GetProperty("capabilities").GetProperty("tools").ValueKind);
        Assert.AreEqual("soul-computer-use", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.AreEqual("0.1.0", result.GetProperty("serverInfo").GetProperty("version").GetString());
    }

    [TestMethod]
    public async Task InitializeFallsBackToTheBaselineVersionForAnUnknownRevision()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Request("7", "initialize", """{"protocolVersion":"1999-01-01"}""")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(McpServer.DefaultProtocolVersion, response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [TestMethod]
    public async Task InitializeWithoutParametersFallsBackToTheBaselineVersion()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            ["""{"jsonrpc":"2.0","id":1,"method":"initialize"}"""]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(McpServer.DefaultProtocolVersion, response.RootElement.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [TestMethod]
    public async Task TheInitializedNotificationProducesNoResponse()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                Request("5", "ping", "{}"),
            ]);

        Assert.HasCount(1, exchange.Responses, "A notification must never be answered.");
        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(5, response.RootElement.GetProperty("id").GetInt32());
    }

    [TestMethod]
    public async Task PingReturnsAnEmptyResultObject()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Request("2", "ping", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.AreEqual(JsonValueKind.Object, result.ValueKind);
        Assert.IsEmpty(result.EnumerateObject(), "ping must return an empty object.");
    }

    [TestMethod]
    public async Task ToolsListPublishesTheSevenContractToolsVerbatim()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Request("3", "tools/list", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement tools = response.RootElement.GetProperty("result").GetProperty("tools");
        Assert.HasCount(8, tools.EnumerateArray(), "The seven contract tools and the adapter's local start_session tool.");

        int index = 0;
        foreach (JsonElement tool in tools.EnumerateArray().Take(7))
        {
            SessionTool expected = SessionToolCatalog.Tools[index];
            Assert.AreEqual(SessionToolNames.ToWireName(expected), tool.GetProperty("name").GetString());
            Assert.AreEqual(SessionToolCatalog.Describe(expected), tool.GetProperty("description").GetString());

            using JsonDocument catalogSchema = JsonDocument.Parse(SessionToolCatalog.SchemaOf(expected));
            Assert.IsTrue(
                JsonElement.DeepEquals(catalogSchema.RootElement, tool.GetProperty("inputSchema")),
                $"{expected} must publish the catalog's schema unchanged.");
            index++;
        }

        Assert.AreEqual(
            "start_session",
            tools.EnumerateArray().Last().GetProperty("name").GetString(),
            "The local tool is appended after the seven contract tools.");
    }

    [TestMethod]
    public async Task ToolsListIncludesTheLocalStartSessionTool()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Request("4", "tools/list", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement tools = response.RootElement.GetProperty("result").GetProperty("tools");
        Assert.HasCount(1, tools.EnumerateArray().Where(tool => tool.GetProperty("name").GetString() == "get_status"));

        JsonElement startSession = tools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "start_session");
        Assert.AreEqual(
            "Open the product window on a new session; the operator logs on themselves",
            startSession.GetProperty("description").GetString());

        using JsonDocument expectedSchema = JsonDocument.Parse(
            """{"type":"object","properties":{},"additionalProperties":false,"required":[]}""");
        Assert.IsTrue(
            JsonElement.DeepEquals(expectedSchema.RootElement, startSession.GetProperty("inputSchema")),
            "start_session takes no arguments and publishes the empty object schema.");
    }

    [TestMethod]
    public async Task StartSessionLaunchesTheHostWithoutReadingStatus()
    {
        RecordingSessionAccessor accessor = new(new RecordingSessionClient());
        FakeHostStarter starter = new(HostStartOutcome.Started);

        ServerExchange exchange = await RunServerAsync(accessor, starter, [Call("6", "start_session", "{}")]);

        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
        Assert.AreEqual(0, accessor.ResolveCount, "start_session must not resolve a session.");
        Assert.AreEqual(1, starter.StartCount);
    }

    [TestMethod]
    public async Task StartSessionReportsOkWhenTheOwnerWasAlreadyRunning()
    {
        FakeHostStarter starter = new(HostStartOutcome.AlreadyRunning);

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            starter,
            [Call("61", "start_session", "{}")]);

        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
        Assert.AreEqual(1, starter.StartCount);
    }

    [TestMethod]
    public async Task AStartSessionWithUnexpectedArgumentsIsAnInvalidRequest()
    {
        RecordingSessionAccessor accessor = new(new RecordingSessionClient());
        FakeHostStarter starter = new(HostStartOutcome.Started);

        ServerExchange exchange = await RunServerAsync(accessor, starter, [Call("62", "start_session", """{"foo":1}""")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(0, starter.StartCount, "A refused call must not start the host.");
        Assert.AreEqual(0, accessor.ResolveCount, "A refused call must not resolve a session.");
    }

    [TestMethod]
    public async Task AStartSessionWithAnEmptyArgumentsObjectSucceeds()
    {
        RecordingSessionAccessor accessor = new(new RecordingSessionClient());
        FakeHostStarter starter = new(HostStartOutcome.Started);

        ServerExchange exchange = await RunServerAsync(accessor, starter, [Call("63", "start_session", "{}")]);

        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
        Assert.AreEqual(1, starter.StartCount);
    }

    [TestMethod]
    [DataRow(SessionErrorCode.LockHeld, "E_LOCK_HELD", false)]
    [DataRow(SessionErrorCode.NotReady, "E_NOT_READY", true)]
    [DataRow(SessionErrorCode.HostLaunchFailed, "E_HOST_LAUNCH_FAILED", false)]
    public async Task AStartSessionRefusalIsAToolErrorWithTheLaunchersCode(
        SessionErrorCode code,
        string wireName,
        bool retryable)
    {
        SessionOperationException refusal = new(code, "A fixed template for the refusal.", retryable);
        FakeHostStarter starter = new(HostStartOutcome.Started, refusal);

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            starter,
            [Call("7", "start_session", "{}")]);

        JsonElement result = ResultOf(exchange);
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());

        using JsonDocument error = JsonDocument.Parse(ContentText(result));
        Assert.AreEqual(wireName, error.RootElement.GetProperty("code").GetString());
        Assert.AreEqual(retryable, error.RootElement.GetProperty("retryable").GetBoolean());
    }

    [TestMethod]
    public async Task ASessionToolWithoutAnOwnerIsAToolErrorAndTheServerStaysAlive()
    {
        RecordingSessionAccessor accessor = new(
            new RecordingSessionClient(),
            new SessionOperationException(
                SessionErrorCode.NotReady,
                "No session owner is running.",
                true));

        ServerExchange exchange = await RunServerAsync(
            accessor,
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("8", "get_status", "{}"), Request("9", "ping", "{}")]);

        Assert.HasCount(2, exchange.Responses, "The server must keep serving after a failed tool call.");

        using JsonDocument failure = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement result = failure.RootElement.GetProperty("result");
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());
        using JsonDocument error = JsonDocument.Parse(ContentText(result));
        Assert.AreEqual("E_NOT_READY", error.RootElement.GetProperty("code").GetString());

        using JsonDocument ping = JsonDocument.Parse(exchange.Responses[1]);
        Assert.AreEqual(9, ping.RootElement.GetProperty("id").GetInt32());
        Assert.IsEmpty(ping.RootElement.GetProperty("result").EnumerateObject());
    }

    [TestMethod]
    public async Task EachSessionToolCallResolvesAndReleasesItsOwnSession()
    {
        RecordingSessionAccessor accessor = new(new RecordingSessionClient());

        ServerExchange exchange = await RunServerAsync(
            accessor,
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("80", "get_status", "{}"), Call("81", "get_status", "{}")]);

        Assert.HasCount(2, exchange.Responses);
        Assert.AreEqual(2, accessor.ResolveCount, "Each tool call resolves its own session.");
        Assert.AreEqual(2, accessor.ReleaseCount, "Each resolved session is released when its call ends.");
    }

    [TestMethod]
    public void StartSessionIsNotAcceptedByTheSharedToolNameParser()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => SessionToolNames.FromWireName("start_session"));
    }

    [TestMethod]
    public async Task GetStatusReturnsTheContractPayload()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("10", "get_status", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement result = response.RootElement.GetProperty("result");
        Assert.IsFalse(result.TryGetProperty("isError", out _), "A successful call does not carry isError.");

        using JsonDocument status = JsonDocument.Parse(ContentText(result));
        JsonElement body = status.RootElement;
        Assert.AreEqual("ready", body.GetProperty("state").GetString());
        Assert.AreEqual(ReadyStatus.SessionId, body.GetProperty("sessionId").GetString());
        Assert.AreEqual("system-credential", body.GetProperty("authTier").GetString());
        Assert.AreEqual("login", body.GetProperty("gateStrength").GetString());
        Assert.AreEqual(1920, body.GetProperty("screen").GetProperty("width").GetInt32());
        Assert.AreEqual(1080, body.GetProperty("screen").GetProperty("height").GetInt32());
        Assert.IsFalse(body.TryGetProperty("helloHint", out _), "The hint appears only when the session reports it.");
        Assert.HasCount(1, client.Calls);
    }

    [TestMethod]
    public async Task GetStatusReportsTheHelloHintWhenTheSessionDoes()
    {
        RecordingSessionClient client = new()
        {
            Status = ReadyStatus with { HelloHint = "helloNotEnrolled" },
        };

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("11", "get_status", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        using JsonDocument status = JsonDocument.Parse(ContentText(response.RootElement.GetProperty("result")));
        Assert.AreEqual("helloNotEnrolled", status.RootElement.GetProperty("helloHint").GetString());
    }

    [TestMethod]
    public async Task ScreenshotReturnsDecodableImageContent()
    {
        byte[] frame = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x01, 0x02, 0x03];
        RecordingSessionClient client = new() { Screenshot = frame };

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("12", "screenshot", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        JsonElement content = response.RootElement.GetProperty("result").GetProperty("content")[0];
        Assert.AreEqual("image", content.GetProperty("type").GetString());
        Assert.AreEqual("image/png", content.GetProperty("mimeType").GetString());
        CollectionAssert.AreEqual(frame, Convert.FromBase64String(content.GetProperty("data").GetString()!));

        CollectionAssert.AreEqual(
            ScreenshotCallOrder,
            client.Calls,
            "The adapter reads the status it reports before taking the frame.");
    }

    [TestMethod]
    public async Task MouseMoveForwardsTheCoordinate()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("20", "mouse_move", """{"x":10,"y":20}""")]);

        Assert.AreEqual((10, 20), client.LastMove);
        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
        CollectionAssert.AreEqual(MoveCallOrder, client.Calls);
    }

    [TestMethod]
    public async Task MouseClickForwardsTheButtonAndCount()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("21", "mouse_click", """{"x":100,"y":200,"button":"right","count":2}""")]);

        Assert.AreEqual((100, 200, MouseButton.Right, 2), client.LastClick);
        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
    }

    [TestMethod]
    public async Task MouseScrollForwardsBothDeltas()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("22", "mouse_scroll", """{"x":5,"y":6,"deltaX":0,"deltaY":-120}""")]);

        Assert.AreEqual((5, 6, 0, -120), client.LastScroll);
        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
    }

    [TestMethod]
    public async Task KeyTypeForwardsTheTextAndNeverEchoesIt()
    {
        const string Secret = "s3cr3t-typed-text";
        RecordingSessionClient client = new();

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("30", "key_type", $$"""{"text":"{{Secret}}"}""")]);

        Assert.AreEqual(Secret, client.LastTypedText);
        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
        Assert.IsFalse(exchange.Responses[0].Contains(Secret, StringComparison.Ordinal), "The typed text must not come back.");
        Assert.IsFalse(exchange.Diagnostics.Contains(Secret, StringComparison.Ordinal), "The typed text must not reach the log.");
    }

    [TestMethod]
    public async Task KeyPressForwardsTheCombinationInOrder()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("31", "key_press", """{"keys":["ctrl","alt","delete"]}""")]);

        CollectionAssert.AreEqual(
            CtrlAltDelete,
            client.LastKeys?.ToArray());
        Assert.AreEqual("{\"ok\":true}", ContentText(ResultOf(exchange)));
    }

    [TestMethod]
    public async Task ACoordinateOutsideTheReportedScreenIsAToolError()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("40", "mouse_move", """{"x":1920,"y":10}""")]);

        JsonElement result = ResultOf(exchange);
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());

        using JsonDocument error = JsonDocument.Parse(ContentText(result));
        Assert.AreEqual("E_COORD_OUT_OF_RANGE", error.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(error.RootElement.GetProperty("retryable").GetBoolean());
        Assert.AreEqual("ready", error.RootElement.GetProperty("state").GetString());
        Assert.IsTrue(error.RootElement.GetProperty("message").GetString()!.Contains("1920", StringComparison.Ordinal));
        CollectionAssert.AreEqual(StatusOnlyCallOrder, client.Calls, "A refused coordinate must not reach the session.");
    }

    [TestMethod]
    public async Task ANegativeCoordinateIsAToolError()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("41", "mouse_click", """{"x":-1,"y":5,"button":"left","count":1}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_COORD_OUT_OF_RANGE", error.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task AnUnknownToolIsRejectedWithInvalidParams()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("50", "shell", "{}")]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task AnUnknownArgumentIsRefusedAsInvalidInput()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("51", "mouse_move", """{"x":1,"y":2,"zoom":3}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
        StringAssert.Contains(error.RootElement.GetProperty("message").GetString(), "zoom");
    }

    [TestMethod]
    public async Task AMissingRequiredArgumentIsRefusedAsInvalidInput()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("52", "mouse_move", """{"x":1}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
        StringAssert.Contains(error.RootElement.GetProperty("message").GetString(), "y");
    }

    [TestMethod]
    public async Task AClickCountOutsideTheContractRangeIsRefused()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("53", "mouse_click", """{"x":1,"y":2,"button":"middle","count":4}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task TextLongerThanTheLimitIsRefusedWithoutEchoingIt()
    {
        string text = new('a', 1001);

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("54", "key_type", $$"""{"text":"{{text}}"}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(exchange.Responses[0].Contains(text, StringComparison.Ordinal), "The refused text must not come back.");
        Assert.IsFalse(exchange.Diagnostics.Contains(text, StringComparison.Ordinal), "The refused text must not reach the log.");
    }

    [TestMethod]
    public async Task AKeyCombinationAboveTheLimitIsRefused()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("55", "key_press", """{"keys":["enter","esc","tab","delete","backspace","home"]}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task AnUnknownKeyNameIsRefusedWithoutEchoingIt()
    {
        const string NotAKey = "password-secret";

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("56", "key_press", $$"""{"keys":["enter","{{NotAKey}}"]}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_INPUT_INVALID", error.RootElement.GetProperty("code").GetString());
        Assert.IsFalse(exchange.Responses[0].Contains(NotAKey, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ANonReadySessionForwardsTheOwnersRefusal()
    {
        RecordingSessionClient client = new()
        {
            Status = ReadyStatus with { State = "awaiting-credentials", AuthTier = AuthTier.SystemCredential, GateStrength = GateStrength.Login },
            ActionFailure = new SessionOperationException(
                SessionErrorCode.AwaitingCredentials,
                "Waiting for the operator to finish the system verification.",
                false),
        };

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("57", "mouse_move", """{"x":5000,"y":10}""")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_AWAITING_CREDENTIALS", error.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("awaiting-credentials", error.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task AClientFailureBecomesAToolErrorWithTheCatalogState()
    {
        RecordingSessionClient client = new()
        {
            StatusFailure = new SessionOperationException(
                SessionErrorCode.Disconnected,
                "No session owner is reachable, so the call was not sent.",
                false),
        };

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("58", "screenshot", "{}")]);

        JsonElement result = ResultOf(exchange);
        Assert.IsTrue(result.GetProperty("isError").GetBoolean());

        using JsonDocument error = JsonDocument.Parse(ContentText(result));
        Assert.AreEqual("E_DISCONNECTED", error.RootElement.GetProperty("code").GetString());
        Assert.AreEqual("disconnected", error.RootElement.GetProperty("state").GetString());
    }

    [TestMethod]
    public async Task AScreenshotAboveTheLimitIsAToolError()
    {
        RecordingSessionClient client = new() { Screenshot = new byte[ScreenshotLimit + 1] };

        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [Call("59", "screenshot", "{}")]);

        using JsonDocument error = JsonDocument.Parse(ContentText(ResultOf(exchange)));
        Assert.AreEqual("E_SCREENSHOT_TOO_LARGE", error.RootElement.GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task MalformedJsonIsAParseErrorWithANullId()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            ["{not json"]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(-32700, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, response.RootElement.GetProperty("id").ValueKind);
    }

    [TestMethod]
    public async Task AnUnknownMethodIsMethodNotFound()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            ["""{"jsonrpc":"2.0","id":9,"method":"resources/list"}"""]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(-32601, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.AreEqual(9, response.RootElement.GetProperty("id").GetInt32());
    }

    [TestMethod]
    public async Task ToolsCallWithoutAnArgumentsObjectIsInvalidParams()
    {
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(new RecordingSessionClient()),
            new FakeHostStarter(HostStartOutcome.Started),
            ["""{"jsonrpc":"2.0","id":60,"method":"tools/call","params":{"name":"mouse_move","arguments":[]}}"""]);

        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        Assert.AreEqual(-32602, response.RootElement.GetProperty("error").GetProperty("code").GetInt32());
    }

    [TestMethod]
    public async Task TheTransportWritesOneUtf8ProtocolLinePerRequest()
    {
        RecordingSessionClient client = new();
        ServerExchange exchange = await RunServerAsync(
            new RecordingSessionAccessor(client),
            new FakeHostStarter(HostStartOutcome.Started),
            [
                Request("70", "initialize", """{"protocolVersion":"2024-11-05"}"""),
                """{"jsonrpc":"2.0","method":"notifications/initialized"}""",
                Call("71", "key_press", """{"keys":["enter"]}"""),
            ]);

        Assert.HasCount(2, exchange.Responses);
        foreach (string line in exchange.Responses)
        {
            using JsonDocument parsed = JsonDocument.Parse(line);
            Assert.AreEqual("2.0", parsed.RootElement.GetProperty("jsonrpc").GetString());
        }

        Assert.AreEqual((byte)'{', exchange.OutputBytes[0], "Protocol output must start with JSON, without a byte-order mark.");
    }

    private static JsonElement ResultOf(ServerExchange exchange)
    {
        Assert.HasCount(1, exchange.Responses);
        using JsonDocument response = JsonDocument.Parse(exchange.Responses[0]);
        return response.RootElement.GetProperty("result").Clone();
    }

    private static string ContentText(JsonElement result)
    {
        return result.GetProperty("content")[0].GetProperty("text").GetString()!;
    }

    private static string Request(string id, string method, string parametersJson)
    {
        return $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":""" + parametersJson + "}";
    }

    private static string Call(string id, string toolName, string argumentsJson)
    {
        return $$"""{"jsonrpc":"2.0","id":{{id}},"method":"tools/call","params":{"name":"{{toolName}}","arguments":""" + argumentsJson + "}}";
    }

    private static async Task<ServerExchange> RunServerAsync(
        ISessionAccessor sessions,
        IHostStarter hostStarter,
        IReadOnlyList<string> requestLines)
    {
        UTF8Encoding encoding = new(encoderShouldEmitUTF8Identifier: false);
        byte[] requestBytes = encoding.GetBytes(string.Join('\n', requestLines) + '\n');

        List<string> responses = [];
        string diagnosticsText;
        byte[] outputBytes;

        using (MemoryStream input = new(requestBytes))
        using (MemoryStream output = new())
        {
            using StringWriter diagnostics = new(CultureInfo.InvariantCulture);
            using (StreamReader reader = new(input, encoding, detectEncodingFromByteOrderMarks: false))
            using (StreamWriter writer = new(output, encoding))
            {
                McpServer server = new(sessions, hostStarter, reader, writer, diagnostics);
                await server.RunAsync(CancellationToken.None);
                await writer.FlushAsync();
            }

            diagnosticsText = diagnostics.ToString();
            outputBytes = output.ToArray();
        }

        foreach (string line in encoding.GetString(outputBytes).Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                responses.Add(trimmed);
            }
        }

        return new ServerExchange(responses, diagnosticsText, outputBytes);
    }

    /// <summary>The captured protocol output and stderr of one server run.</summary>
    private sealed record ServerExchange(IReadOnlyList<string> Responses, string Diagnostics, byte[] OutputBytes);

    /// <summary>
    /// An <see cref="ISessionClient"/> that records every call and can be told to fail. It reports a
    /// ready session unless a test overrides the status or arms a failure.
    /// </summary>
    private sealed class RecordingSessionClient : ISessionClient
    {
        internal SessionStatus Status { get; set; } = ReadyStatus;

        internal byte[] Screenshot { get; set; } = [0x89, 0x50, 0x4E, 0x47];

        internal SessionOperationException? StatusFailure { get; set; }

        internal SessionOperationException? ActionFailure { get; set; }

        internal List<string> Calls { get; } = [];

        internal (int X, int Y)? LastMove { get; private set; }

        internal (int X, int Y, MouseButton Button, int Count)? LastClick { get; private set; }

        internal (int X, int Y, int DeltaX, int DeltaY)? LastScroll { get; private set; }

        internal string? LastTypedText { get; private set; }

        internal IReadOnlyList<SessionKey>? LastKeys { get; private set; }

        public Task<SessionStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            Calls.Add("get_status");
            SessionOperationException? failure = StatusFailure;
            return failure is null ? Task.FromResult(Status) : Task.FromException<SessionStatus>(failure);
        }

        public Task<byte[]> ScreenshotAsync(CancellationToken cancellationToken)
        {
            Calls.Add("screenshot");
            return Task.FromResult(Screenshot);
        }

        public Task MouseMoveAsync(int x, int y, CancellationToken cancellationToken)
        {
            Calls.Add("mouse_move");
            LastMove = (x, y);
            return Fail();
        }

        public Task MouseClickAsync(int x, int y, MouseButton button, int count, CancellationToken cancellationToken)
        {
            Calls.Add("mouse_click");
            LastClick = (x, y, button, count);
            return Fail();
        }

        public Task MouseScrollAsync(int x, int y, int deltaX, int deltaY, CancellationToken cancellationToken)
        {
            Calls.Add("mouse_scroll");
            LastScroll = (x, y, deltaX, deltaY);
            return Fail();
        }

        public Task KeyTypeAsync(string text, CancellationToken cancellationToken)
        {
            Calls.Add($"key_type:{text.Length}");
            LastTypedText = text;
            return Fail();
        }

        public Task KeyPressAsync(IReadOnlyList<SessionKey> keys, CancellationToken cancellationToken)
        {
            Calls.Add("key_press");
            LastKeys = keys;
            return Fail();
        }

        private Task Fail()
        {
            SessionOperationException? failure = ActionFailure;
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        }
    }

    /// <summary>
    /// An <see cref="ISessionAccessor"/> that records how often a session was resolved and either
    /// hands out one resolved session per call or fails with an armed error.
    /// </summary>
    private sealed class RecordingSessionAccessor : ISessionAccessor
    {
        private readonly RecordingSessionClient _client;
        private readonly SessionOperationException? _failure;

        internal RecordingSessionAccessor(RecordingSessionClient client)
            : this(client, null)
        {
        }

        internal RecordingSessionAccessor(RecordingSessionClient client, SessionOperationException? failure)
        {
            _client = client;
            _failure = failure;
        }

        internal int ResolveCount { get; private set; }

        internal int ReleaseCount { get; private set; }

        public Task<ResolvedSession> ResolveAsync(CancellationToken cancellationToken)
        {
            ResolveCount++;
            SessionOperationException? failure = _failure;
            return failure is null
                ? Task.FromResult(new ResolvedSession(_client, () =>
                {
                    ReleaseCount++;
                    return ValueTask.CompletedTask;
                }))
                : Task.FromException<ResolvedSession>(failure);
        }
    }

    /// <summary>
    /// An <see cref="IHostStarter"/> that records how often a start was requested and returns an
    /// outcome, or fails with an armed error.
    /// </summary>
    private sealed class FakeHostStarter : IHostStarter
    {
        private readonly HostStartOutcome _outcome;
        private readonly SessionOperationException? _failure;

        internal FakeHostStarter(HostStartOutcome outcome)
            : this(outcome, null)
        {
        }

        internal FakeHostStarter(HostStartOutcome outcome, SessionOperationException? failure)
        {
            _outcome = outcome;
            _failure = failure;
        }

        internal int StartCount { get; private set; }

        public Task<HostStartOutcome> StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            SessionOperationException? failure = _failure;
            return failure is null
                ? Task.FromResult(_outcome)
                : Task.FromException<HostStartOutcome>(failure);
        }
    }
}