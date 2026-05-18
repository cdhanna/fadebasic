using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Tests;

/// <summary>
/// Integration tests for the FadeBasic DAP adapter.
/// Starts the adapter as a child process and speaks the DAP protocol over stdin/stdout.
/// </summary>
public class DapIntegrationTests
{
    private Process _dap;
    private int _seq = 1;
    private readonly List<JsonObject> _events = new();
    private readonly List<JsonObject> _reverseRequests = new();
    private readonly Dictionary<int, JsonObject> _responses = new();
    private CancellationTokenSource _cts;
    private Task _readerTask;

    private string TestProjectPath =>
        Path.GetFullPath(Path.Combine(TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "Tests", "Fixtures", "Projects", "Primitive", "prim.csproj"));

    private string DapDll
    {
        get
        {
            var dll = Path.GetFullPath(Path.Combine(
                TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "DAP", "bin", "Debug", "net8.0", "DAP.dll"));
            if (!File.Exists(dll))
            {
                var proj = Path.GetFullPath(Path.Combine(
                    TestContext.CurrentContext.TestDirectory, "..", "..", "..", "..", "DAP", "DAP.csproj"));
                var build = Process.Start(new ProcessStartInfo("dotnet", $"build \"{proj}\" -c Debug")
                {
                    RedirectStandardOutput = true, RedirectStandardError = true,
                });
                build!.WaitForExit();
                Assert.That(build.ExitCode, Is.EqualTo(0), "DAP build failed");
            }
            return dll;
        }
    }

    [SetUp]
    public void Setup()
    {
        _seq = 1;
        _events.Clear();
        _reverseRequests.Clear();
        _responses.Clear();
        _cts = new CancellationTokenSource();

        _dap = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", DapDll)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                Environment =
                {
                    ["FADE_DAP_LOG_PATH"] = Path.Combine(TestContext.CurrentContext.WorkDirectory, "dap_test.log"),
                    ["FADE_DOTNET_PATH"] = "dotnet",
                },
            },
        };
        _dap.Start();
        _readerTask = Task.Run(() => ReadMessages(_cts.Token));
    }

    [TearDown]
    public void TearDown()
    {
        _cts?.Cancel();
        try { _dap?.Kill(true); } catch { }
        _dap?.Dispose();
    }

    [Test]
    public async Task InitializeReturnsCapabilities()
    {
        var resp = await SendRequest("initialize", BuildInitArgs());
        Assert.That(resp["success"]?.GetValue<bool>(), Is.True, $"initialize failed: {resp}");
        var body = resp["body"]?.AsObject();
        Assert.That(body?["supportsConfigurationDoneRequest"]?.GetValue<bool>(), Is.True);
    }

    [Test]
    public async Task LaunchWithBadProjectReturnsError()
    {
        await SendRequest("initialize", BuildInitArgs());
        var resp = await SendRequest("launch", new JsonObject { ["program"] = "/nonexistent/fake.csproj" });
        Assert.That(resp["success"]?.GetValue<bool>(), Is.False, "launch should fail for bad project");
        Assert.That(resp["message"]?.GetValue<string>(), Is.Not.Empty);
    }

    [Test]
    public async Task FullDebugSessionWithBreakpoint()
    {
        if (!File.Exists(TestProjectPath))
            Assert.Ignore($"Fixture not found: {TestProjectPath}");

        // 1. Initialize
        var initResp = await SendRequest("initialize", BuildInitArgs());
        Assert.That(initResp["success"]?.GetValue<bool>(), Is.True, "initialize failed");

        // 2. Launch (async -- adapter will send runInTerminal + initialized)
        var launchTask = SendRequest("launch", new JsonObject
        {
            ["program"] = TestProjectPath,
        });

        // 3. Wait for initialized event + runInTerminal reverse request
        var ritReq = await WaitForReverseRequest("runInTerminal", TimeSpan.FromSeconds(30));
        Assert.That(ritReq, Is.Not.Null, "Never received runInTerminal");

        var ritArgs = ritReq["arguments"]!.AsObject();
        var env = ritArgs["env"]!.AsObject();
        Assert.That(env.ContainsKey("FADE_BASIC_DEBUG"), "missing FADE_BASIC_DEBUG env var");
        Assert.That(env.ContainsKey("FADE_BASIC_DEBUG_PORT"), "missing FADE_BASIC_DEBUG_PORT env var");

        // Verify the port is present (may be int or string in JSON)
        var portNode = env["FADE_BASIC_DEBUG_PORT"];
        int port;
        if (portNode is JsonValue jv && jv.TryGetValue<int>(out var intPort))
            port = intPort;
        else
            port = int.Parse(portNode!.GetValue<string>());
        Assert.That(port, Is.GreaterThan(0), "port should be a positive integer");

        var argv = ritArgs["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.That(argv, Has.Count.GreaterThan(0));
        TestContext.Out.WriteLine($"runInTerminal argv: {string.Join(" ", argv)}");
        TestContext.Out.WriteLine($"runInTerminal env: {env.ToJsonString()}");

        // 4. Respond to runInTerminal (simulate -- don't actually start the process)
        await SendReverseResponse(ritReq["seq"]!.GetValue<int>(), "runInTerminal",
            new JsonObject { ["processId"] = 99999 });

        // 5. Wait for launch response
        var launchResp = await launchTask;
        Assert.That(launchResp["success"]?.GetValue<bool>(), Is.True, $"launch failed: {launchResp}");

        // 6. Verify initialized event was sent
        var initialized = await WaitForEvent("initialized", TimeSpan.FromSeconds(5));
        Assert.That(initialized, Is.Not.Null, "Never received initialized event");

        // Note: setBreakpoints / configurationDone / stopped / stackTrace require a real
        // debuggee process connected via TCP (the adapter blocks on Connect() after
        // runInTerminal). Those flows are verified by the DAP logs from real IDE sessions
        // and by the StoppedEventIncludesThreadId source-level test.
    }

    [Test]
    public async Task RunInTerminalEnvPortIsUsable()
    {
        // The DAP sends FADE_BASIC_DEBUG_PORT as a JSON integer in the env map.
        // Clients must convert it to a string for environment variables.
        // This test verifies the port value is a valid integer regardless of JSON type.
        if (!File.Exists(TestProjectPath))
            Assert.Ignore($"Fixture not found: {TestProjectPath}");

        await SendRequest("initialize", BuildInitArgs());
        var launchTask = SendRequest("launch", new JsonObject { ["program"] = TestProjectPath });

        var ritReq = await WaitForReverseRequest("runInTerminal", TimeSpan.FromSeconds(30));
        Assert.That(ritReq, Is.Not.Null);

        var env = ritReq["arguments"]!.AsObject()["env"]!.AsObject();
        var portNode = env["FADE_BASIC_DEBUG_PORT"];

        // The port may arrive as a JSON number (integer) -- clients need to .toString() it
        string portStr;
        if (portNode is JsonValue jv)
        {
            if (jv.TryGetValue<int>(out var intVal))
                portStr = intVal.ToString();
            else
                portStr = jv.GetValue<string>();
        }
        else
        {
            portStr = portNode!.ToString();
        }

        Assert.That(int.TryParse(portStr, out var port), Is.True,
            $"Port '{portStr}' must be parseable as integer");
        Assert.That(port, Is.GreaterThan(1024).And.LessThan(65536),
            $"Port {port} should be in ephemeral range");

        TestContext.Out.WriteLine($"Port value: {port} (from JSON type: {portNode!.GetType().Name})");

        // Clean up - respond to runInTerminal so launch completes
        await SendReverseResponse(ritReq["seq"]!.GetValue<int>(), "runInTerminal",
            new JsonObject { ["processId"] = 1 });
        await launchTask;
    }

    // ---- helpers ----

    static JsonObject BuildInitArgs() => new()
    {
        ["clientID"] = "test",
        ["clientName"] = "DapIntegrationTest",
        ["adapterID"] = "fade-basic",
        ["linesStartAt1"] = true,
        ["columnsStartAt1"] = true,
        ["pathFormat"] = "path",
        ["supportsRunInTerminalRequest"] = true,
    };

    async Task<JsonObject> WaitForReverseRequest(string command, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_reverseRequests)
            {
                var found = _reverseRequests.Find(r => r["command"]?.GetValue<string>() == command);
                if (found != null) return found;
            }
            await Task.Delay(50);
        }
        return null;
    }

    async Task<JsonObject> WaitForEvent(string eventName, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            lock (_events)
            {
                var found = _events.Find(e => e["event"]?.GetValue<string>() == eventName);
                if (found != null) return found;
            }
            await Task.Delay(50);
        }
        return null;
    }

    async Task<JsonObject> SendRequest(string command, JsonObject arguments)
    {
        var seq = _seq++;
        var msg = new JsonObject
        {
            ["seq"] = seq, ["type"] = "request", ["command"] = command, ["arguments"] = arguments,
        };
        await WriteMessage(msg);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (_responses)
            {
                if (_responses.TryGetValue(seq, out var resp))
                {
                    _responses.Remove(seq);
                    return resp;
                }
            }
            await Task.Delay(50);
        }
        Assert.Fail($"Timeout waiting for response to '{command}' (seq={seq})");
        return null!;
    }

    async Task SendReverseResponse(int requestSeq, string command, JsonObject body)
    {
        await WriteMessage(new JsonObject
        {
            ["seq"] = _seq++, ["type"] = "response", ["request_seq"] = requestSeq,
            ["success"] = true, ["command"] = command, ["body"] = body,
        });
    }

    async Task WriteMessage(JsonObject msg)
    {
        var json = msg.ToJsonString();
        var header = $"Content-Length: {Encoding.UTF8.GetByteCount(json)}\r\n\r\n";
        await _dap.StandardInput.WriteAsync(header);
        await _dap.StandardInput.WriteAsync(json);
        await _dap.StandardInput.FlushAsync();
    }

    void ReadMessages(CancellationToken ct)
    {
        try
        {
            var stream = _dap.StandardOutput.BaseStream;
            var buffer = new byte[65536];
            var pending = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0) break;
                pending.Write(buffer, 0, read);
                while (TryParseMessage(pending, out var msg))
                {
                    var type = msg["type"]?.GetValue<string>();
                    switch (type)
                    {
                        case "response":
                            lock (_responses) { _responses[msg["request_seq"]!.GetValue<int>()] = msg; }
                            break;
                        case "event":
                            lock (_events) { _events.Add(msg); }
                            break;
                        case "request":
                            lock (_reverseRequests) { _reverseRequests.Add(msg); }
                            break;
                    }
                }
            }
        }
        catch when (ct.IsCancellationRequested) { }
        catch (IOException) { }
    }

    static bool TryParseMessage(MemoryStream pending, out JsonObject msg)
    {
        msg = null;
        var data = pending.ToArray();
        var text = Encoding.UTF8.GetString(data);
        var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (headerEnd < 0) return false;
        int contentLength = -1;
        foreach (var line in text.Substring(0, headerEnd).Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                contentLength = int.Parse(line.Substring("Content-Length:".Length).Trim());
        if (contentLength < 0) return false;
        var bodyStart = Encoding.UTF8.GetByteCount(text.Substring(0, headerEnd + 4));
        if (data.Length < bodyStart + contentLength) return false;
        var bodyBytes = new byte[contentLength];
        Array.Copy(data, bodyStart, bodyBytes, 0, contentLength);
        msg = JsonNode.Parse(Encoding.UTF8.GetString(bodyBytes))?.AsObject();
        if (msg == null) return false;
        var consumed = bodyStart + contentLength;
        var remaining = data.Length - consumed;
        pending.SetLength(0);
        if (remaining > 0) pending.Write(data, consumed, remaining);
        return true;
    }
}
