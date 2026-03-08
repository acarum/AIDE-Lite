// ============================================================================
// AIDE Lite - AI-powered IDE extension for Mendix Studio Pro
// Copyright (c) 2025-2026 Neel Desai / Golden Earth Software Consulting Inc.
// Common interface for LLM API providers (Claude, OpenAI, etc.)
// ============================================================================
using AideLite.Models;

namespace AideLite.Services;

/// <summary>
/// Common interface for language model API services.
/// Allows switching between different providers (Claude, OpenAI) transparently.
/// </summary>
public interface IAIApiService
{
    /// <summary>
    /// Send a streaming request to the LLM API with tool definitions.
    /// Retries automatically on rate-limit and transient errors.
    /// </summary>
    /// <param name="systemPrompt">System prompt parts (instructions and app context)</param>
    /// <param name="messages">Conversation messages in API format</param>
    /// <param name="tools">Tool definitions for function calling</param>
    /// <param name="onTextDelta">Callback for each text token received</param>
    /// <param name="onToolStart">Callback when a tool call starts (toolName, toolId)</param>
    /// <param name="onRetryWait">Callback before retry delays (attempt, delaySec, maxRetries)</param>
    /// <param name="externalToken">Cancellation token</param>
    /// <returns>API response with text, tool calls, and token usage</returns>
    Task<ApiResponse> SendStreamingRequestAsync(
        SystemPromptParts systemPrompt,
        List<object> messages,
        List<Dictionary<string, object>>? tools,
        Action<string> onTextDelta,
        Action<string, string> onToolStart,
        Action<int, int, int>? onRetryWait = null,
        CancellationToken externalToken = default);

    /// <summary>
    /// Cancel any in-flight request.
    /// </summary>
    void Cancel();
}
