using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using Sharpcode.Core;

namespace Sharpcode.Tests;

public sealed class AgentTests : IDisposable
{
    private readonly DirectoryInfo folder = Directory.CreateTempSubdirectory("sharpcode-agent-");

    [Theory]
    [InlineData(ModelProvider.OpenAI)]
    [InlineData(ModelProvider.OpenAICompatible)]
    [InlineData(ModelProvider.LMStudio)]
    public async Task OpenAiProtocolStreamsToolCallsAndStagesMultipleFiles(ModelProvider provider)
    {
        var token = TestContext.Current.CancellationToken;
        var workspace = new Workspace(folder.FullName);
        await File.WriteAllTextAsync(workspace.Resolve("main.cs"), "old-value", token);
        using var handler = new FakeProvider(
            VerifyAndReply(body =>
            {
                Assert.True(body.GetProperty("stream").GetBoolean());
                Assert.Equal(5, body.GetProperty("tools").GetArrayLength());
            }, OpenAiTools()),
            VerifyAndReply(body =>
            {
                var messages = body.GetProperty("messages");
                Assert.Equal("tool", messages[3].GetProperty("role").GetString());
                Assert.Equal("write", messages[3].GetProperty("tool_call_id").GetString());
                Assert.Equal("new", messages[4].GetProperty("tool_call_id").GetString());
            }, Event(new { choices = new[] { new { delta = new { content = "Drafted updates." }, finish_reason = "stop" } } }) +
                "data: [DONE]\n\n"));
        using var http = new HttpClient(handler);
        var agent = new CodingAgent(new ModelClient(http));
        var text = new StringBuilder();

        var result = await agent.RunAsync(new(provider, "fixture", "http://localhost:9292/v1"), "test-key",
            AgentMode.Edit, workspace, [], "Draft two changes", null, value => text.Append(value), _ => { }, token);

        Assert.Equal("Drafted updates.", text.ToString());
        Assert.Collection(result.Changes, file => Assert.Equal("changed-value", file.Text), file => Assert.True(file.IsNew));
        Assert.Equal("old-value", await File.ReadAllTextAsync(workspace.Resolve("main.cs"), token));
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("/v1/chat/completions", request.Path);
            Assert.Equal("Bearer test-key", request.Authorization);
        });
    }

    [Fact]
    public async Task ClaudeUsesMessagesWithGroupedToolResultsAndStreamsText()
    {
        var token = TestContext.Current.CancellationToken;
        var workspace = new Workspace(folder.FullName);
        await File.WriteAllTextAsync(workspace.Resolve("first.txt"), "test", token);
        using var handler = new FakeProvider(
            VerifyAndReply(body =>
            {
                Assert.Equal(3, body.GetProperty("tools").GetArrayLength());
                Assert.DoesNotContain("replace_text", body.GetProperty("tools").GetRawText());
                Assert.Contains("Sharpcode", body.GetProperty("system").GetString());
            }, ClaudeTool(0, "first", "list_files", """{"filter":"first"}""") +
                ClaudeTool(1, "second", "read_file", """{"path":"first.txt"}""") + Event(new { type = "message_stop" })),
            VerifyAndReply(body =>
            {
                var messages = body.GetProperty("messages");
                Assert.Equal(3, messages.GetArrayLength());
                var results = messages[2].GetProperty("content");
                Assert.Equal("first", results[0].GetProperty("tool_use_id").GetString());
                Assert.Contains("1: test", results[1].GetProperty("content").GetString());
            }, Event(new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Found it." } }) +
                Event(new { type = "message_stop" })));
        using var http = new HttpClient(handler);

        var result = await new CodingAgent(new ModelClient(http)).RunAsync(
            new(ModelProvider.Claude, "fixture"), "test-key", AgentMode.Ask, workspace, [], "Read first.txt",
            null, _ => { }, _ => { }, token);

        Assert.Equal("Found it.", result.Text);
        Assert.Empty(result.Changes);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("/v1/messages", request.Path);
            Assert.Equal("test-key", request.ClaudeKey);
        });
    }

    [Fact]
    public async Task IncompleteStreamIsReportedAsAnError()
    {
        using var handler = new FakeProvider(VerifyAndReply(_ => { },
            Event(new { choices = new[] { new { delta = new { content = "partial" } } } })));
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<IOException>(() => new ModelClient(http).CompleteAsync(
            new(ModelProvider.LMStudio, "fixture"), null, "", [new("user", "hello")], [], null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StalledProviderBodyTimesOutAfterHeaders()
    {
        var pipe = new Pipe();
        using var handler = new FakeProvider((_, _) => Task.FromResult(StreamResponse(pipe.Reader.AsStream())));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        try
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => new ModelClient(http).CompleteAsync(
                new(ModelProvider.LMStudio, "fixture"), null, "", [new("user", "hello")], [], null,
                TestContext.Current.CancellationToken));
            Assert.Contains("stopped sending", exception.Message);
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task StreamHandlesSplitUtf8AndFinishesEvenWhenConnectionStaysOpen()
    {
        var token = TestContext.Current.CancellationToken;
        var pipe = new Pipe();
        using var handler = new FakeProvider((_, _) => Task.FromResult(StreamResponse(pipe.Reader.AsStream())));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var completion = new ModelClient(http).CompleteAsync(
                new(ModelProvider.LMStudio, "fixture"), null, "", [new("user", "hello")], [], null, token);
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes(
                "\uFEFFdata: {\"choices\":[{\"delta\":{\"content\":\""), token);
            var emoji = Encoding.UTF8.GetBytes("🌱");
            await pipe.Writer.WriteAsync(emoji.AsMemory(0, 2), token);
            await pipe.Writer.WriteAsync(emoji.AsMemory(2), token);
            await pipe.Writer.WriteAsync(Encoding.UTF8.GetBytes("\"},\"finish_reason\":\"stop\"}]}\r\n\r\n"), token);

            Assert.Equal("🌱", (await completion).Text);
        }
        finally { await pipe.Writer.CompleteAsync(); }
    }

    [Fact]
    public async Task NonStreamingProviderCannotReturnAnOversizedCompletion()
    {
        var payload = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = new string('a', 512_001) } } } });
        using var handler = new FakeProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        }));
        using var http = new HttpClient(handler);
        var exception = await Assert.ThrowsAsync<IOException>(() => new ModelClient(http).CompleteAsync(
            new(ModelProvider.LMStudio, "fixture"), null, "", [new("user", "hello")], [], _ => Assert.Fail("Output was published"),
            TestContext.Current.CancellationToken));
        Assert.Contains("output limit", exception.Message);
    }

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new("text/event-stream");
        return response;
    }

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> VerifyAndReply(
        Action<JsonElement> inspect, string stream) => async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            inspect(body.RootElement);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(stream, Encoding.UTF8, "text/event-stream")
            };
        };

    private static string OpenAiTools()
    {
        static string Part(object[] calls) => Event(new { choices = new[] { new { delta = new { tool_calls = calls } } } });
        return Part([
            new { index = 0, id = "write", function = new { name = "replace_text", arguments = """{"path":"main.cs","old_text":""" } },
            new { index = 1, id = "new", function = new { name = "create_file", arguments = """{"path":"docs.md","content":"# Docs"}""" } }
        ]) + Part([new { index = 0, function = new { arguments = """ "old-value","new_text":"changed-value"}""" } }]) +
            "data: [DONE]\n\n";
    }

    private static string ClaudeTool(int index, string id, string name, string arguments) =>
        Event(new { type = "content_block_start", index, content_block = new { type = "tool_use", id, name, input = new { } } }) +
        Event(new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = arguments } });

    private static string Event(object payload) => "data: " + JsonSerializer.Serialize(payload) + "\n\n";

    public void Dispose() => folder.Delete(recursive: true);

    private sealed record SeenRequest(string Path, string? Authorization, string? ClaudeKey);

    private sealed class FakeProvider(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] replies)
        : HttpMessageHandler
    {
        public List<SeenRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Requests.Count;
            Requests.Add(new(request.RequestUri!.AbsolutePath, request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("x-api-key", out var values) ? values.Single() : null));
            return replies[index](request, cancellationToken);
        }
    }
}
