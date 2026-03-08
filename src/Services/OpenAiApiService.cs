// ============================================================================
// AIDE Lite - AI-powered IDE extension for Mendix Studio Pro
// Copyright (c) 2025-2026 Neel Desai / Golden Earth Software Consulting Inc.
// OpenAI API integration — streaming SSE requests with tool-use support
// ============================================================================
using AideLite.Models;
using Mendix.StudioPro.ExtensionsAPI;
using Mendix.StudioPro.ExtensionsAPI.Services;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static System.Net.WebRequestMethods;

namespace AideLite.Services;

[SupportedOSPlatform("windows")]
public class OpenAiApiService : IAIApiService
{
    private readonly IHttpClientService _httpClientService;
    private readonly ConfigurationService _configService;
    private readonly ILogService _logService;
    private CancellationTokenSource? _currentCts;

    private const string ApiUrl = "https://api.siemens.com/llm/v1/chat/completions"; // ;"https://api.openai.com/v1/chat/completions";
    private const int MaxStreamTextBytes = 2 * 1024 * 1024;
    private const int MaxToolInputJsonBytes = 512 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public OpenAiApiService(
        IHttpClientService httpClientService,
        ConfigurationService configService,
        ILogService logService)
    {
        _httpClientService = httpClientService;
        _configService = configService;
        _logService = logService;
    }

    // ?? Public API ??????????????????????????????????????????????????????

    /// <summary>
    /// Send a streaming request to the OpenAI API with tool definitions.
    /// Retries automatically on rate-limit (429), overloaded (529), and transient 5xx errors.
    /// </summary>
    public async Task<ApiResponse> SendStreamingRequestAsync(
        SystemPromptParts systemPrompt,
        List<object> messages,
        List<Dictionary<string, object>>? tools,
        Action<string> onTextDelta,
        Action<string, string> onToolStart,
        Action<int, int, int>? onRetryWait = null,
        CancellationToken externalToken = default)
    {
        var apiKey = _configService.GetApiKey();
        if (string.IsNullOrEmpty(apiKey))
            return ApiResponse.Error("No API key configured. Please set your OpenAI API key in Settings.");

        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        var ct = _currentCts.Token;

        try
        {
            var config = _configService.GetConfig();
            var requestJson = BuildRequestJson(systemPrompt, messages, tools, config);
            _logService.Info($"AIDE Lite: [OpenAI API] Model: {config.SelectedModel}, MaxTokens: {config.MaxTokens}, body: {requestJson.Length} chars");
            _logService.Info($"AIDE Lite: [OpenAI API] System prompt sizes: instructions={systemPrompt.StaticInstructions.Length}chars, context={systemPrompt.AppContext.Length}chars");

            return await SendWithRetries(apiKey, requestJson, config, onTextDelta, onToolStart, onRetryWait, ct);
        }
        catch (OperationCanceledException)
        {
            return ApiResponse.Error("Request cancelled.", "cancelled");
        }
        catch (HttpRequestException ex)
        {
            _logService.Error($"AIDE Lite: Network error: {ex.Message}");
            return ApiResponse.Error("Network error. Please check your connection.", "network");
        }
        catch (Exception ex)
        {
            _logService.Error($"AIDE Lite: Unexpected error: {ex.Message}");
            return ApiResponse.Error("An unexpected error occurred. Check the AIDE Lite log for details.");
        }
        finally
        {
            var cts = Interlocked.Exchange(ref _currentCts, null);
            cts?.Dispose();
        }
    }

    public void Cancel()
    {
        try { _currentCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // ?? Request building ????????????????????????????????????????????????

    private static string BuildRequestJson(
        SystemPromptParts systemPrompt, List<object> messages,
        List<Dictionary<string, object>>? tools, AideLiteConfig config)
    {
        // Convert system prompt to OpenAI format
        var systemMessage = new Dictionary<string, object>
        {
            ["role"] = "system",
            ["content"] = systemPrompt.StaticInstructions + "\n\n" + systemPrompt.AppContext
        };

        // Prepend system message to the messages list
        var allMessages = new List<object> { systemMessage };
        allMessages.AddRange(messages);

        var body = new Dictionary<string, object>
        {
            ["model"] = config.SelectedModel,
            ["max_tokens"] = config.MaxTokens,
            ["stream"] = true,
            ["messages"] = allMessages
        };

        if (tools is { Count: > 0 })
        {
            // Convert tools to OpenAI format
            var openAiTools = tools.Select(tool => new Dictionary<string, object>
            {
                ["type"] = "function",
                ["function"] = tool
            }).ToList();
            body["tools"] = openAiTools;
        }

        return JsonSerializer.Serialize(body, JsonOptions);
    }

    // ?? Retry loop ??????????????????????????????????????????????????????

    private static bool IsRetryableStatus(int code) =>
        code is 429 or 529 or 500 or 502 or 503;

    private async Task<ApiResponse> SendWithRetries(
        string apiKey, string requestJson, AideLiteConfig config,
        Action<string> onTextDelta, Action<string, string> onToolStart,
        Action<int, int, int>? onRetryWait, CancellationToken ct)
    {
        var maxRetries = config.RetryMaxAttempts;
        var retryDelay = config.RetryDelaySeconds;
        _logService.Info($"AIDE Lite: [OpenAI API] Retry config: max={maxRetries}, delay={retryDelay}s");

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            _logService.Info($"AIDE Lite: [OpenAI API] Attempt {attempt + 1}/{maxRetries + 1}...");

            using var httpClient = _httpClientService.CreateHttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(5);

            using var request = CreateHttpRequest(apiKey, requestJson);
            using var response = await httpClient.SendAsync(request, ct);
            var statusCode = (int)response.StatusCode;
            _logService.Info($"AIDE Lite: [OpenAI API] HTTP {statusCode} {response.StatusCode}");

            if (response.IsSuccessStatusCode)
            {
                var result = await ParseStream(response, onTextDelta, onToolStart, ct);
                _logService.Info($"AIDE Lite: [OpenAI API] Tokens - input={result.InputTokens}, output={result.OutputTokens}");
                return result;
            }

            var errorBody = await response.Content.ReadAsStringAsync(ct);
            var truncatedBody = errorBody.Length > 500 ? errorBody[..500] + "...(truncated)" : errorBody;
            _logService.Error($"AIDE Lite: [OpenAI API] Error body: {truncatedBody}");

            if (statusCode == 401)
                return ApiResponse.Error("Invalid API key. Please check your settings.", "auth_error");

            if (!IsRetryableStatus(statusCode) || attempt >= maxRetries)
                return FinalErrorForStatus(statusCode, maxRetries);

            var delaySec = GetRetryDelay(response, retryDelay, attempt);
            _logService.Info($"AIDE Lite: [OpenAI API] Retryable {statusCode}, waiting {delaySec}s...");
            onRetryWait?.Invoke(attempt + 1, delaySec, maxRetries);
            await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
        }

        return FinalErrorForStatus(0, maxRetries);
    }

    private static HttpRequestMessage CreateHttpRequest(string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Headers.Add("Authorization", $"Bearer {apiKey}");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static ApiResponse FinalErrorForStatus(int statusCode, int maxRetries)
    {
        if (statusCode is 429 or 529)
            return ApiResponse.Error(
                $"Rate limit exceeded after {maxRetries} retries. Try increasing retry settings or check your API usage limits.",
                "rate_limit");

        return ApiResponse.Error($"API error ({statusCode}). Check the AIDE Lite log for details.");
    }

    private static int GetRetryDelay(HttpResponseMessage response, int configuredDelay, int attempt)
    {
        if (response.Headers.TryGetValues("retry-after", out var values))
        {
            var retryAfter = values.FirstOrDefault();
            if (int.TryParse(retryAfter, out var headerSeconds) && headerSeconds > 0)
                return Math.Min(headerSeconds, 300);
        }
        // Exponential backoff with jitter when no retry-after header
        var exponentialDelay = configuredDelay * Math.Pow(2, attempt);
        var jitter = Random.Shared.NextDouble() * 0.3 * exponentialDelay;
        return (int)Math.Min(exponentialDelay + jitter, 300);
    }

    // ?? SSE stream parsing ??????????????????????????????????????????????
    //
    // OpenAI streams Server-Sent Events (SSE) with chunks containing:
    //   - delta.content for text
    //   - delta.tool_calls for function calls
    //   - usage information in the final chunk

    private async Task<ApiResponse> ParseStream(
        HttpResponseMessage response,
        Action<string> onTextDelta,
        Action<string, string> onToolStart,
        CancellationToken ct)
    {
        var state = new StreamState();

        using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (string.IsNullOrEmpty(line) || !line.StartsWith("data: "))
                continue;

            var data = line[6..];
            if (data == "[DONE]")
                break;

            var earlyExit = ProcessSseEvent(data, state, onTextDelta, onToolStart);
            if (earlyExit != null)
                return earlyExit;
        }

        // Finalize any pending tool calls
        FinalizeToolCalls(state);

        state.Result.FullText = state.FullText.ToString();
        state.Result.IsSuccess = string.IsNullOrEmpty(state.Result.ErrorMessage);
        return state.Result;
    }

    private static ApiResponse? ProcessSseEvent(
        string data, StreamState s,
        Action<string> onTextDelta, Action<string, string> onToolStart)
    {
        try
        {
            var evt = JsonNode.Parse(data);
            if (evt == null) return null;

            var choices = evt["choices"]?.AsArray();
            if (choices == null || choices.Count == 0) return null;

            var choice = choices[0];
            var delta = choice?["delta"];
            var finishReason = choice?["finish_reason"]?.GetValue<string>();

            if (!string.IsNullOrEmpty(finishReason))
                s.Result.StopReason = finishReason;

            // Handle text content
            var content = delta?["content"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(content))
            {
                if (s.FullText.Length + content.Length > MaxStreamTextBytes)
                    return SizeLimitError("Response exceeded maximum size limit.", s);

                s.FullText.Append(content);
                onTextDelta(content);
            }

            // Handle tool calls
            var toolCalls = delta?["tool_calls"]?.AsArray();
            if (toolCalls != null)
            {
                foreach (var toolCall in toolCalls)
                {
                    var index = toolCall?["index"]?.GetValue<int>() ?? 0;
                    var id = toolCall?["id"]?.GetValue<string>();
                    var type = toolCall?["type"]?.GetValue<string>();
                    var function = toolCall?["function"];
                    var functionName = function?["name"]?.GetValue<string>();
                    var functionArgs = function?["arguments"]?.GetValue<string>();

                    // Ensure we have a slot for this tool call
                    while (s.PendingToolCalls.Count <= index)
                    {
                        s.PendingToolCalls.Add(new PendingToolCall());
                    }

                    var pending = s.PendingToolCalls[index];

                    // Set ID and name if provided (first chunk for this tool call)
                    if (!string.IsNullOrEmpty(id))
                    {
                        pending.Id = id;
                        s.PendingToolCalls[index] = pending;
                    }

                    if (!string.IsNullOrEmpty(functionName))
                    {
                        pending.Name = functionName;
                        s.PendingToolCalls[index] = pending;
                        
                        // Notify that tool started (first time we see this tool)
                        if (!pending.NotifiedStart)
                        {
                            onToolStart(functionName, pending.Id);
                            pending.NotifiedStart = true;
                            s.PendingToolCalls[index] = pending;
                        }
                    }

                    // Accumulate arguments
                    if (!string.IsNullOrEmpty(functionArgs))
                    {
                        if (pending.Arguments.Length + functionArgs.Length > MaxToolInputJsonBytes)
                            return SizeLimitError("Tool input exceeded maximum size limit.", s);

                        pending.Arguments.Append(functionArgs);
                        s.PendingToolCalls[index] = pending;
                    }
                }
            }

            // Handle usage information
            var usage = evt["usage"];
            if (usage != null)
            {
                var promptTokens = usage["prompt_tokens"]?.GetValue<int>();
                if (promptTokens.HasValue)
                    s.Result.InputTokens = promptTokens.Value;

                var completionTokens = usage["completion_tokens"]?.GetValue<int>();
                if (completionTokens.HasValue)
                    s.Result.OutputTokens = completionTokens.Value;
            }

            return null;
        }
        catch (JsonException ex)
        {
            return new ApiResponse
            {
                IsSuccess = false,
                ErrorMessage = $"JSON parsing error: {ex.Message}",
                ErrorCode = "json_error",
                FullText = s.FullText.ToString()
            };
        }
    }

    private static void FinalizeToolCalls(StreamState s)
    {
        foreach (var pending in s.PendingToolCalls)
        {
            if (!string.IsNullOrEmpty(pending.Id))
            {
                s.Result.ToolCalls.Add(new ToolCall
                {
                    Id = pending.Id,
                    Name = pending.Name,
                    InputJson = pending.Arguments.ToString()
                });
            }
        }
    }

    private static ApiResponse SizeLimitError(string message, StreamState s) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        ErrorCode = "stream_too_large",
        FullText = s.FullText.ToString()
    };

    // ?? Stream parse state ??????????????????????????????????????????????

    private struct PendingToolCall
    {
        public string Id;
        public string Name;
        public StringBuilder Arguments;
        public bool NotifiedStart;

        public PendingToolCall()
        {
            Id = string.Empty;
            Name = string.Empty;
            Arguments = new StringBuilder();
            NotifiedStart = false;
        }
    }

    private class StreamState
    {
        public readonly ApiResponse Result = new();
        public readonly StringBuilder FullText = new();
        public readonly List<PendingToolCall> PendingToolCalls = new();
    }
}
