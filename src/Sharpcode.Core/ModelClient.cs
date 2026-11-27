using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Sharpcode.Core;

public enum ModelProvider { OpenAI, Claude, OpenAICompatible, LMStudio }

public sealed record ModelSettings(ModelProvider Provider, string Model, string BaseUrl = "", bool KeepHistory = false)
{
    public static ModelSettings Default { get; } = new(ModelProvider.LMStudio, "");

    public string Endpoint => string.IsNullOrWhiteSpace(BaseUrl) ? DefaultEndpoint(Provider) : BaseUrl.Trim();
    public string? EnvironmentKey => System.Environment.GetEnvironmentVariable(Provider switch
    {
        ModelProvider.OpenAI => "OPENAI_API_KEY",
        ModelProvider.Claude => "ANTHROPIC_API_KEY",
        ModelProvider.LMStudio => "LM_STUDIO_API_TOKEN",
        _ => "SHARPCODE_API_KEY"
    });

    public static string DefaultEndpoint(ModelProvider provider) => provider switch
    {
        ModelProvider.OpenAI => "https://api.openai.com/v1",
        ModelProvider.Claude => "https://api.anthropic.com/v1",
        ModelProvider.LMStudio => "http://localhost:1234/v1",
        _ => "http://localhost:11434/v1"
    };
}

public sealed record ToolDefinition(string Name, string Description, JsonElement Schema);
public sealed record ToolCall(string Id, string Name, string Arguments);
public sealed record ChatMessage(string Role, string Text, IReadOnlyList<ToolCall>? Calls = null, string? ToolId = null);

public sealed class ModelClient(HttpClient http)
{
    private const int MaxResponseBytes = 8 * 1024 * 1024;
    private const int MaxEventCharacters = 2_000_000;
    private const int MaxOutputCharacters = 512_000;
    private const int MaxToolCalls = 128;

    public async Task<IReadOnlyList<string>> ModelsAsync(ModelSettings settings, string? key, CancellationToken cancellationToken)
    {
        using var request = Request(HttpMethod.Get, settings, key, "models");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var deadline = BodyDeadline(cancellationToken);
        try
        {
            await CheckAsync(response, deadline.Token).ConfigureAwait(false);
            using var json = await ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
            return json.RootElement.GetProperty("data").EnumerateArray()
                .Select(model => String(model, "id")).Where(id => id.Length > 0).Order().ToArray();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The provider stopped sending the model list.");
        }
    }

    public async Task<ChatMessage> CompleteAsync(ModelSettings settings, string? key, string system,
        IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, Action<string>? onText,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("Choose a model in provider settings.");

        var claude = settings.Provider == ModelProvider.Claude;
        using var request = Request(HttpMethod.Post, settings, key, claude ? "messages" : "chat/completions");
        var body = claude
            ? ClaudeBody(settings.Model.Trim(), system, messages, tools)
            : OpenAiBody(settings.Model.Trim(), system, messages, tools);
        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        using var deadline = BodyDeadline(cancellationToken);
        try
        {
            await CheckAsync(response, deadline.Token).ConfigureAwait(false);
            return await ReadCompletionAsync(response, claude, onText, deadline).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The provider stopped sending the response.");
        }
    }

    private CancellationTokenSource BodyDeadline(CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(http.Timeout);
        return deadline;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await content.LoadIntoBufferAsync(MaxResponseBytes, cancellationToken).ConfigureAwait(false);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<ChatMessage> ReadCompletionAsync(HttpResponseMessage response, bool claude, Action<string>? onText,
        CancellationTokenSource deadline)
    {
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            using var json = await ReadJsonAsync(response.Content, deadline.Token).ConfigureAwait(false);
            var result = ReadNonStreaming(json.RootElement, claude);
            CheckOutputSize(result.Text.Length, result.Calls?.Count ?? 0,
                result.Calls?.Sum(call => (long)call.Id.Length + call.Name.Length + call.Arguments.Length) ?? 0);
            if (result.Text.Length > 0)
                onText?.Invoke(result.Text);
            return result;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
        var calls = new SortedDictionary<int, CallBuilder>();
        var text = new StringBuilder();
        var eventBody = new StringBuilder();
        var finished = false;
        await foreach (var line in ReadStreamLinesAsync(stream, deadline).ConfigureAwait(false))
        {
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (eventBody.Length > 0)
                    eventBody.Append('\n');
                eventBody.Append(line.AsSpan(5).TrimStart());
                if (eventBody.Length > MaxEventCharacters)
                    throw new IOException("The provider sent an oversized stream event.");
                continue;
            }
            if (line.Length != 0 || eventBody.Length == 0)
                continue;

            var data = eventBody.ToString();
            eventBody.Clear();
            if (data == "[DONE]")
            {
                finished = true;
                break;
            }
            using var json = JsonDocument.Parse(data);
            var item = json.RootElement;
            if (item.TryGetProperty("error", out var error))
                throw new IOException($"Provider error: {String(error, "message")}");

            var delta = claude ? ReadClaudeDelta(item, calls) : ReadOpenAiDelta(item, calls);
            finished |= claude
                ? String(item, "type") == "message_stop"
                : item.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                  choices[0].TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String;
            if (delta.Length > 0)
                text.Append(delta);
            CheckOutputSize(text.Length, calls.Count, calls.Values.Sum(call => call.Length));
            if (delta.Length > 0)
                onText?.Invoke(delta);
            if (finished)
                break;
        }

        if (!finished)
            throw new IOException("The provider stream ended before the response was complete.");
        return new("assistant", text.ToString(), calls.Values.Select(call => call.Build()).ToArray());
    }

    private async IAsyncEnumerable<string> ReadStreamLinesAsync(Stream stream, CancellationTokenSource deadline)
    {
        var buffer = new byte[4096];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
        var decoder = Encoding.UTF8.GetDecoder();
        var line = new StringBuilder();
        var afterCarriageReturn = false;
        var atStart = true;
        var receivedBytes = 0;
        while (true)
        {
            deadline.CancelAfter(http.Timeout);
            var count = await stream.ReadAsync(buffer.AsMemory(), deadline.Token).ConfigureAwait(false);
            if (count == 0)
                break;
            receivedBytes += count;
            if (receivedBytes > MaxResponseBytes)
                throw new IOException("The provider stream exceeded Sharpcode's response limit.");

            var decoded = decoder.GetChars(buffer.AsSpan(0, count), characters, flush: false);
            for (var offset = 0; offset < decoded; offset++)
            {
                var character = characters[offset];
                var isByteOrderMark = atStart && character == '\uFEFF';
                atStart = false;
                if (isByteOrderMark)
                    continue;
                var skipLineFeed = afterCarriageReturn && character == '\n';
                afterCarriageReturn = character == '\r';
                if (skipLineFeed)
                    continue;
                if (character is '\r' or '\n')
                {
                    yield return line.ToString();
                    line.Clear();
                    continue;
                }
                if (line.Length == MaxEventCharacters)
                    throw new IOException("The provider sent an oversized stream line.");
                line.Append(character);
            }
        }
    }

    private static void CheckOutputSize(int textLength, int toolCount, long toolLength)
    {
        if (textLength + toolLength > MaxOutputCharacters || toolCount > MaxToolCalls)
            throw new IOException("The provider response exceeded Sharpcode's output limit.");
    }

    private static HttpRequestMessage Request(HttpMethod method, ModelSettings settings, string? sessionKey, string route)
    {
        if (!Uri.TryCreate(settings.Endpoint.TrimEnd('/') + "/" + route, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("https" or "http") || endpoint.UserInfo.Length != 0 ||
            endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
            throw new InvalidOperationException("Enter a valid HTTP(S) API base URL without credentials, a query, or a fragment.");

        var key = string.IsNullOrWhiteSpace(sessionKey) ? settings.EnvironmentKey : sessionKey.Trim();
        if (string.IsNullOrWhiteSpace(key) && settings.Provider is ModelProvider.OpenAI or ModelProvider.Claude)
            throw new InvalidOperationException("Enter an API key or set the provider's environment variable.");

        var request = new HttpRequestMessage(method, endpoint);
        if (settings.Provider == ModelProvider.Claude)
        {
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("x-api-key", key!);
        }
        else if (!string.IsNullOrWhiteSpace(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        return request;
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream);
        var message = new char[1600];
        var count = await reader.ReadBlockAsync(message, cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException($"Provider returned HTTP {(int)response.StatusCode}. {new string(message, 0, count)}");
    }

    private static object OpenAiBody(string model, string system, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var conversation = new List<object> { new { role = "system", content = system } };
        foreach (var message in messages)
        {
            if (message.Role == "tool")
                conversation.Add(new { role = "tool", tool_call_id = message.ToolId, content = message.Text });
            else if (message.Calls is { Count: > 0 } calls)
                conversation.Add(new { role = "assistant", content = message.Text, tool_calls = calls.Select(call =>
                    new { id = call.Id, type = "function", function = new { name = call.Name, arguments = call.Arguments } }) });
            else
                conversation.Add(new { role = message.Role, content = message.Text });
        }

        return tools.Count == 0
            ? new { model, messages = conversation, stream = true }
            : new { model, messages = conversation, stream = true, tools = tools.Select(tool =>
                new { type = "function", function = new { name = tool.Name, description = tool.Description, parameters = tool.Schema } }) };
    }

    private static object ClaudeBody(string model, string system, IReadOnlyList<ChatMessage> messages,
        IReadOnlyList<ToolDefinition> tools)
    {
        var conversation = new List<object>();
        for (var index = 0; index < messages.Count; index++)
        {
            var message = messages[index];
            if (message.Role == "tool")
            {
                var results = new List<object>();
                do
                {
                    message = messages[index++];
                    results.Add(new { type = "tool_result", tool_use_id = message.ToolId, content = message.Text });
                } while (index < messages.Count && messages[index].Role == "tool");
                index--;
                conversation.Add(new { role = "user", content = results });
                continue;
            }
            if (message.Calls is { Count: > 0 } calls)
            {
                var blocks = new List<object>();
                if (message.Text.Length > 0)
                    blocks.Add(new { type = "text", text = message.Text });
                blocks.AddRange(calls.Select(call => new { type = "tool_use", id = call.Id, name = call.Name,
                    input = ParseToolArguments(call.Arguments) }));
                conversation.Add(new { role = "assistant", content = blocks });
            }
            else if (message.Text.Length > 0)
                conversation.Add(new { role = message.Role, content = message.Text });
        }

        return tools.Count == 0
            ? new { model, system, messages = conversation, max_tokens = 4096, stream = true }
            : new { model, system, messages = conversation, max_tokens = 4096, stream = true, tools = tools.Select(tool =>
                new { name = tool.Name, description = tool.Description, input_schema = tool.Schema }) };
    }

    private static JsonElement ParseToolArguments(string text)
    {
        try { return JsonSerializer.Deserialize<JsonElement>(text); }
        catch (JsonException) { return JsonSerializer.SerializeToElement(new { }); }
    }

    private static string ReadOpenAiDelta(JsonElement item, SortedDictionary<int, CallBuilder> calls)
    {
        if (!item.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0 ||
            !choices[0].TryGetProperty("delta", out var delta))
            return "";

        if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var tool in toolCalls.EnumerateArray())
            {
                var index = tool.GetProperty("index").GetInt32();
                if (!calls.TryGetValue(index, out var call))
                    calls[index] = call = new();
                var id = String(tool, "id");
                if (id.Length > 0)
                    call.Id = id;
                if (tool.TryGetProperty("function", out var function))
                {
                    call.Name.Append(String(function, "name"));
                    call.Arguments.Append(String(function, "arguments"));
                }
            }
        }
        return String(delta, "content");
    }

    private static string ReadClaudeDelta(JsonElement item, SortedDictionary<int, CallBuilder> calls)
    {
        switch (String(item, "type"))
        {
            case "content_block_start":
                var content = item.GetProperty("content_block");
                if (String(content, "type") == "tool_use")
                {
                    var call = new CallBuilder { Id = String(content, "id") };
                    if (content.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
                        call.InitialArguments = input.GetRawText();
                    call.Name.Append(String(content, "name"));
                    calls[item.GetProperty("index").GetInt32()] = call;
                }
                return String(content, "text");
            case "content_block_delta":
                var delta = item.GetProperty("delta");
                if (calls.TryGetValue(item.GetProperty("index").GetInt32(), out var builder))
                    builder.Arguments.Append(String(delta, "partial_json"));
                return String(delta, "text");
            default:
                return "";
        }
    }

    private static ChatMessage ReadNonStreaming(JsonElement item, bool claude)
    {
        if (claude)
        {
            var blocks = item.GetProperty("content").EnumerateArray().ToArray();
            return new("assistant", string.Concat(blocks.Select(block => String(block, "text"))),
                blocks.Where(block => String(block, "type") == "tool_use")
                    .Select(block => new ToolCall(String(block, "id"), String(block, "name"), block.GetProperty("input").GetRawText())).ToArray());
        }
        var message = item.GetProperty("choices")[0].GetProperty("message");
        var calls = message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind == JsonValueKind.Array
            ? tools.EnumerateArray().Select(tool => new ToolCall(String(tool, "id"),
                String(tool.GetProperty("function"), "name"), String(tool.GetProperty("function"), "arguments"))).ToArray()
            : [];
        return new("assistant", String(message, "content"), calls);
    }

    private static string String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private sealed class CallBuilder
    {
        public string Id { get; set; } = "";
        public string InitialArguments { get; set; } = "{}";
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
        public long Length => (long)Id.Length + Name.Length + Arguments.Length + InitialArguments.Length;
        public ToolCall Build() => new(Id.Length == 0 ? $"call_{Guid.NewGuid():N}" : Id, Name.ToString(),
            Arguments.Length == 0 ? InitialArguments : Arguments.ToString());
    }
}
