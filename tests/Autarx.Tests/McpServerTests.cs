using System.Text.Json;
using Autarx.Core.Mcp;
using Xunit;

namespace Autarx.Tests;

public class McpServerTests : IDisposable
{
    private readonly string _workspace;

    public McpServerTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "autarx-mcp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_workspace);
        foreach (var source in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "Fixtures", "DiffBefore"), "*.arxml"))
            File.Copy(source, Path.Combine(_workspace, Path.GetFileName(source)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (IOException) { }
    }

    private static (JsonElement Response, List<JsonElement> All) Serve(params string[] lines)
    {
        using var input = new StringReader(string.Join("\n", lines));
        using var output = new StringWriter();

        McpServer.Serve(input, output, "1.0.0");

        var responses = output.ToString()!
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => JsonDocument.Parse(l).RootElement.Clone())
            .ToList();
        return (responses.FirstOrDefault(JsonElementExtensions.IsObject), responses);
    }

    private static string ToolCall(int id, string name, string args) =>
        $"{{\"jsonrpc\":\"2.0\",\"id\":{id},\"method\":\"tools/call\",\"params\":{{\"name\":\"{name}\",\"arguments\":{args}}}}}";

    private static string Args(string pairs) => pairs;

    [Fact]
    public void Initialize_handshakes_with_protocol_version()
    {
        var (response, all) = Serve(
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""",
            """{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        Assert.Single(all); // the notification stays unanswered
        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal("autarx", response.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.True(response.GetProperty("result").GetProperty("protocolVersion").GetString()!.Length > 0);
    }

    [Fact]
    public void Tools_list_exposes_the_full_api()
    {
        var (response, _) = Serve("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");

        var tools = response.GetProperty("result").GetProperty("tools");
        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();

        Assert.Contains("inspect", names);
        Assert.Contains("diff", names);
        Assert.Contains("impact", names);
        Assert.Contains("comm", names);
        Assert.Contains("patch_plan", names);
        Assert.Contains("patch_apply", names);
        Assert.Equal(12, names.Count);
    }

    [Fact]
    public void Tool_call_over_the_full_pipeline()
    {
        var (response, _) = Serve(ToolCall(3, "find",
            "{\"workspace\":\"" + Escape(_workspace) + "\",\"pattern\":\"Radar\"}"));

        var content = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.Contains("/Vehicle/Ecus/RadarFL", content);
        Assert.False(response.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public void Patch_plan_and_apply_through_the_review_boundary()
    {
        var ops = "[{\"op\":\"set-parameter\",\"target\":\"/Config/Diag\",\"definition\":\"/AURIX2G/Diag/DiagGeneral/DiagParamA\",\"value\":\"9\"}]";

        var (plan, _) = Serve(ToolCall(4, "patch_plan",
            "{\"workspace\":\"" + Escape(_workspace) + "\",\"operations\":" + ops + "}"));

        Assert.False(plan.GetProperty("result").GetProperty("isError").GetBoolean());
        Assert.Contains("\"valid\": true", plan.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        var (applied, _) = Serve(ToolCall(5, "patch_apply",
            "{\"workspace\":\"" + Escape(_workspace) + "\",\"operations\":" + ops + "}"));

        Assert.Contains("\"applied\": true", applied.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public void Every_call_lands_in_the_workspace_audit_log()
    {
        Serve(ToolCall(6, "ecus", "{\"workspace\":\"" + Escape(_workspace) + "\"}"));

        var auditFile = Path.Combine(_workspace, ".autarx", "audit.jsonl");
        Assert.True(File.Exists(auditFile));
        Assert.Contains("\"tool\": \"ecus\"", File.ReadAllText(auditFile));
    }

    [Fact]
    public void Unknown_tool_is_a_tool_error_not_a_protocol_error()
    {
        var (response, _) = Serve("""{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"nope","arguments":{}}}""");

        Assert.True(response.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    private static string Escape(string path) => path.Replace("\\", "\\\\");

    private static class JsonElementExtensions
    {
        public static bool IsObject(JsonElement element) =>
            element.ValueKind == JsonValueKind.Object;
    }
}
