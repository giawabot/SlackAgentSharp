using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackAgentSharp;

public sealed class SlackClient : IDisposable
{
    private enum RetryMode
    {
        RateLimitOnly,
        TransientRetry
    }

    private const string SlackApiBaseUrl = "https://slack.com/api/";
    private readonly HttpClient _httpClient;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly TimeSpan _requestTimeout;
    private readonly int _transientRetryCount;
    private readonly TimeSpan _retryDelay;
    private readonly int _maxResponseBodyBytes;
    private bool _disposed;

    /// <summary>
    /// Creates a Slack API client using the provided options.
    /// </summary>
    /// <param name="options">Client configuration and authentication settings.</param>
    public SlackClient(SlackOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (string.IsNullOrWhiteSpace(options.BotToken))
        {
            throw new ArgumentException("Slack bot token is required.", nameof(options));
        }

        if (options.RequestTimeoutSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Request timeout must be greater than zero.");
        }

        if (options.TransientRetryCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Transient retry count cannot be negative.");
        }

        if (options.RetryDelayMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Retry delay cannot be negative.");
        }

        if (options.MaxResponseBodyBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Max response body bytes must be greater than zero.");
        }

        _requestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        _transientRetryCount = options.TransientRetryCount;
        _retryDelay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds);
        _maxResponseBodyBytes = options.MaxResponseBodyBytes;

        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(SlackApiBaseUrl, UriKind.Absolute),
            // Per-request CTS timeouts are enforced in SendWithRetryAsync for retry-aware timing.
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.BotToken);

        _serializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
    }

    /// <summary>
    /// Opens a direct message channel with a user and sends a message.
    /// </summary>
    /// <param name="userId">Slack user ID to message.</param>
    /// <param name="message">Message text to send.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the message; otherwise <c>false</c>.</returns>
    public async Task<bool> SendDirectMessageAsync(
        string userId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("Slack user ID is required.", nameof(userId));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Slack message is required.", nameof(message));
        }

        var channelId = await OpenDirectMessageChannelAsync(userId, cancellationToken);
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return false;
        }

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), _serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Opens or retrieves a direct message channel for the specified user.
    /// </summary>
    /// <param name="userId">Slack user ID to open a DM with.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The channel ID when successful; otherwise <c>null</c>.</returns>
    public async Task<string?> OpenDirectMessageChannelAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new SlackConversationOpenRequest(userId), _serializerOptions);
        using var response = await SendPostAsync("conversations.open", payload, cancellationToken, RetryMode.RateLimitOnly);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var openResponse = JsonSerializer.Deserialize<SlackConversationOpenResponse>(responseBody, _serializerOptions);
        if (openResponse is null || !openResponse.Ok)
        {
            return null;
        }

        return openResponse.Channel?.Id;
    }

    /// <summary>
    /// Gets messages from a Slack conversation, optionally filtered by oldest timestamp.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="oldestTimestamp">Oldest timestamp boundary, or <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A read-only list of messages, or an empty list on failure.</returns>
    public async Task<IReadOnlyList<SlackMessage>> GetConversationMessagesAsync(
        string channelId,
        string? oldestTimestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        var requestUri = new StringBuilder("conversations.history?channel=");
        requestUri.Append(Uri.EscapeDataString(channelId));
        if (!string.IsNullOrWhiteSpace(oldestTimestamp))
        {
            requestUri.Append("&oldest=");
            requestUri.Append(Uri.EscapeDataString(oldestTimestamp));
        }

        using var response = await SendGetAsync(requestUri.ToString(), cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var historyResponse = JsonSerializer.Deserialize<SlackConversationHistoryResponse>(responseBody, _serializerOptions);
        if (historyResponse is null || !historyResponse.Ok)
        {
            return [];
        }

        return historyResponse.Messages ?? [];
    }

    /// <summary>
    /// Gets replies in a Slack thread, optionally filtered by oldest timestamp.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="oldestTimestamp">Oldest timestamp boundary, or <c>null</c>.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A read-only list of thread messages, or an empty list on failure.</returns>
    public async Task<IReadOnlyList<SlackMessage>> GetConversationRepliesAsync(
        string channelId,
        string threadTimestamp,
        string? oldestTimestamp,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        var requestUri = new StringBuilder("conversations.replies?channel=");
        requestUri.Append(Uri.EscapeDataString(channelId));
        requestUri.Append("&ts=");
        requestUri.Append(Uri.EscapeDataString(threadTimestamp));
        if (!string.IsNullOrWhiteSpace(oldestTimestamp))
        {
            requestUri.Append("&oldest=");
            requestUri.Append(Uri.EscapeDataString(oldestTimestamp));
        }

        using var response = await SendGetAsync(requestUri.ToString(), cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return [];
        }

        if (!response.IsSuccessStatusCode)
        {
            return [];
        }

        var repliesResponse = JsonSerializer.Deserialize<SlackConversationHistoryResponse>(responseBody, _serializerOptions);
        if (repliesResponse is null || !repliesResponse.Ok)
        {
            return [];
        }

        return repliesResponse.Messages ?? [];
    }

    /// <summary>
    /// Sends a message to a Slack channel.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="message">Message text to send.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the message; otherwise <c>false</c>.</returns>
    public async Task<bool> SendMessageAsync(
        string channelId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Slack message is required.", nameof(message));
        }

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), _serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Sends a message to an existing Slack thread.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="message">Message text to send.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the message; otherwise <c>false</c>.</returns>
    public async Task<bool> SendThreadMessageAsync(
        string channelId,
        string threadTimestamp,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Slack message is required.", nameof(message));
        }

        var payload = JsonSerializer.Serialize(
            new SlackThreadMessageRequest(channelId, message, threadTimestamp),
            _serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Sets status metadata for an assistant thread in Slack.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="status">Status string understood by Slack.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the update; otherwise <c>false</c>.</returns>
    public async Task<bool> SetAssistantThreadStatusAsync(
        string channelId,
        string threadTimestamp,
        string status,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        var payload = JsonSerializer.Serialize(
            new SlackAssistantThreadStatusRequest(channelId, threadTimestamp, status),
            _serializerOptions);
        var response = await SendAssistantInternalAsync("assistant.threads.setStatus", payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Sets title metadata for an assistant thread in Slack.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="title">Title string understood by Slack.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the update; otherwise <c>false</c>.</returns>
    public async Task<bool> SetAssistantThreadTitleAsync(
        string channelId,
        string threadTimestamp,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Slack thread title is required.", nameof(title));
        }

        var payload = JsonSerializer.Serialize(
            new SlackAssistantThreadTitleRequest(channelId, threadTimestamp, title),
            _serializerOptions);
        var response = await SendAssistantInternalAsync("assistant.threads.setTitle", payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Sets suggested prompts for an assistant thread in Slack.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="title">Title shown above prompt suggestions.</param>
    /// <param name="prompts">Suggested prompt buttons.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the update; otherwise <c>false</c>.</returns>
    public async Task<bool> SetAssistantThreadSuggestedPromptsAsync(
        string channelId,
        string threadTimestamp,
        string title,
        IReadOnlyList<SlackSuggestedPrompt> prompts,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Suggested prompt title is required.", nameof(title));
        }

        if (prompts is null || prompts.Count == 0)
        {
            throw new ArgumentException("At least one suggested prompt is required.", nameof(prompts));
        }

        var payload = JsonSerializer.Serialize(
            new SlackAssistantThreadSuggestedPromptsRequest(channelId, threadTimestamp, title, prompts),
            _serializerOptions);
        var response = await SendAssistantInternalAsync("assistant.threads.setSuggestedPrompts", payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Starts a Slack streaming message session.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Thread timestamp.</param>
    /// <param name="markdownText">Initial markdown text, or <c>null</c>.</param>
    /// <param name="recipientTeamId">Optional recipient team ID.</param>
    /// <param name="recipientUserId">Optional recipient user ID.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The stream timestamp when successful; otherwise <c>null</c>.</returns>
    public async Task<string?> StartMessageStreamAsync(
        string channelId,
        string threadTimestamp,
        string? markdownText = null,
        string? recipientTeamId = null,
        string? recipientUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(threadTimestamp))
        {
            throw new ArgumentException("Slack thread timestamp is required.", nameof(threadTimestamp));
        }

        var payload = JsonSerializer.Serialize(
            new SlackStreamStartRequest(channelId, threadTimestamp, markdownText, recipientTeamId, recipientUserId),
            _serializerOptions);
        var response = await SendStreamInternalAsync("chat.startStream", payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

    /// <summary>
    /// Appends markdown text to an active Slack streaming message.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="streamTimestamp">Streaming message timestamp.</param>
    /// <param name="markdownText">Markdown text to append.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the append; otherwise <c>false</c>.</returns>
    public async Task<bool> AppendMessageStreamAsync(
        string channelId,
        string streamTimestamp,
        string markdownText,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(streamTimestamp))
        {
            throw new ArgumentException("Slack stream timestamp is required.", nameof(streamTimestamp));
        }

        if (string.IsNullOrWhiteSpace(markdownText))
        {
            return true;
        }

        var payload = JsonSerializer.Serialize(
            new SlackStreamAppendRequest(channelId, streamTimestamp, markdownText),
            _serializerOptions);
        var response = await SendStreamInternalAsync("chat.appendStream", payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Stops an active Slack streaming message.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="streamTimestamp">Streaming message timestamp.</param>
    /// <param name="markdownText">Optional final markdown text.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the stop request; otherwise <c>false</c>.</returns>
    public async Task<bool> StopMessageStreamAsync(
        string channelId,
        string streamTimestamp,
        string? markdownText = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(streamTimestamp))
        {
            throw new ArgumentException("Slack stream timestamp is required.", nameof(streamTimestamp));
        }

        var payload = JsonSerializer.Serialize(
            new SlackStreamStopRequest(channelId, streamTimestamp, markdownText),
            _serializerOptions);
        var response = await SendStreamInternalAsync("chat.stopStream", payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Sends a message and returns the Slack timestamp when available.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="message">Message text to send.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The message timestamp when successful; otherwise <c>null</c>.</returns>
    public async Task<string?> SendMessageWithTimestampAsync(
        string channelId,
        string message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("Slack message is required.", nameof(message));
        }

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), _serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

    /// <summary>
    /// Sends a block-based message to Slack.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="message">Fallback message text, or <c>null</c>.</param>
    /// <param name="blocks">Slack block payload.</param>
    /// <param name="threadTimestamp">Optional thread timestamp.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>The message timestamp when successful; otherwise <c>null</c>.</returns>
    public async Task<string?> SendMessageWithBlocksAsync(
        string channelId,
        string? message,
        IReadOnlyList<object> blocks,
        string? threadTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (blocks is null || blocks.Count == 0)
        {
            throw new ArgumentException("Slack message blocks are required.", nameof(blocks));
        }

        var payload = JsonSerializer.Serialize(
            new SlackBlockMessageRequest(channelId, message, blocks, threadTimestamp),
            _serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

    /// <summary>
    /// Updates an existing Slack message with new block content.
    /// </summary>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="messageTimestamp">Timestamp of the message to update.</param>
    /// <param name="message">Optional fallback text.</param>
    /// <param name="blocks">Updated Slack block payload.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns><c>true</c> when Slack accepts the update; otherwise <c>false</c>.</returns>
    public async Task<bool> UpdateMessageBlocksAsync(
        string channelId,
        string messageTimestamp,
        string? message,
        IReadOnlyList<object> blocks,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            throw new ArgumentException("Slack channel ID is required.", nameof(channelId));
        }

        if (string.IsNullOrWhiteSpace(messageTimestamp))
        {
            throw new ArgumentException("Slack message timestamp is required.", nameof(messageTimestamp));
        }

        if (blocks is null || blocks.Count == 0)
        {
            throw new ArgumentException("Slack message blocks are required.", nameof(blocks));
        }

        var payload = JsonSerializer.Serialize(
            new SlackBlockMessageUpdateRequest(channelId, messageTimestamp, message, blocks),
            _serializerOptions);
        var response = await SendChatUpdateInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    /// <summary>
    /// Disposes the underlying HTTP client and releases resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    private async Task<SlackApiResponse> SendMessageInternalAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync("chat.postMessage", payload, cancellationToken, RetryMode.RateLimitOnly);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        var messageResponse = TryDeserializeSlackApiResponse(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, BuildHttpError(response.StatusCode, messageResponse?.Error));
        }

        if (messageResponse is null || !messageResponse.Ok)
        {
            return new SlackApiResponse(false, null, messageResponse?.Error ?? "invalid_json");
        }

        return messageResponse;
    }

    private async Task<SlackApiResponse> SendStreamInternalAsync(
        string endpoint,
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(endpoint, payload, cancellationToken, RetryMode.RateLimitOnly);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        var streamResponse = TryDeserializeSlackApiResponse(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, BuildHttpError(response.StatusCode, streamResponse?.Error));
        }

        if (streamResponse is null || !streamResponse.Ok)
        {
            return new SlackApiResponse(false, null, streamResponse?.Error ?? "invalid_json");
        }

        return streamResponse;
    }

    private async Task<SlackApiResponse> SendChatUpdateInternalAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync("chat.update", payload, cancellationToken, RetryMode.RateLimitOnly);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        var updateResponse = TryDeserializeSlackApiResponse(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, BuildHttpError(response.StatusCode, updateResponse?.Error));
        }

        if (updateResponse is null || !updateResponse.Ok)
        {
            return new SlackApiResponse(false, null, updateResponse?.Error ?? "invalid_json");
        }

        return updateResponse;
    }

    private async Task<SlackApiResponse> SendAssistantInternalAsync(
        string endpoint,
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(endpoint, payload, cancellationToken, RetryMode.RateLimitOnly);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        var statusResponse = TryDeserializeSlackApiResponse(responseBody);
        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, BuildHttpError(response.StatusCode, statusResponse?.Error));
        }

        if (statusResponse is null || !statusResponse.Ok)
        {
            return new SlackApiResponse(false, null, statusResponse?.Error ?? "invalid_json");
        }

        return statusResponse;
    }

    private SlackApiResponse? TryDeserializeSlackApiResponse(string responseBody)
    {
        try
        {
            return JsonSerializer.Deserialize<SlackApiResponse>(responseBody, _serializerOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildHttpError(HttpStatusCode statusCode, string? slackError)
    {
        if (!string.IsNullOrWhiteSpace(slackError))
        {
            return slackError;
        }

        return $"http_{(int)statusCode}";
    }

    private Task<HttpResponseMessage> SendGetAsync(string requestUri, CancellationToken cancellationToken)
    {
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, requestUri),
            RetryMode.TransientRetry,
            cancellationToken);
    }

    private Task<HttpResponseMessage> SendPostAsync(
        string endpoint,
        string payload,
        CancellationToken cancellationToken,
        RetryMode retryMode)
    {
        return SendWithRetryAsync(
            () =>
            {
                var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                return request;
            },
            retryMode,
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        RetryMode retryMode,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= _transientRetryCount; attempt++)
        {
            using var request = requestFactory();
            // Combine caller cancellation with per-attempt timeout to bound each retry window.
            using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutToken.CancelAfter(_requestTimeout);
            try
            {
                var response = await _httpClient.SendAsync(
                    request,
                    // Start processing once headers arrive; body size is enforced separately below.
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutToken.Token);

                if (attempt < _transientRetryCount && ShouldRetryStatusCode(response.StatusCode, retryMode))
                {
                    var delay = GetRetryDelay(response, attempt);
                    response.Dispose();
                    await Task.Delay(delay, cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (
                retryMode == RetryMode.TransientRetry
                && !cancellationToken.IsCancellationRequested
                && attempt < _transientRetryCount)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
            catch (HttpRequestException) when (
                retryMode == RetryMode.TransientRetry
                && attempt < _transientRetryCount)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("Retry loop exited unexpectedly.");
    }

    private static bool ShouldRetryStatusCode(HttpStatusCode statusCode, RetryMode retryMode)
    {
        return retryMode switch
        {
            RetryMode.RateLimitOnly => statusCode == HttpStatusCode.TooManyRequests,
            RetryMode.TransientRetry => IsTransientStatusCode(statusCode),
            _ => false
        };
    }

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
    {
        return statusCode == HttpStatusCode.RequestTimeout
            || statusCode == HttpStatusCode.TooManyRequests
            || statusCode == HttpStatusCode.BadGateway
            || statusCode == HttpStatusCode.ServiceUnavailable
            || statusCode == HttpStatusCode.GatewayTimeout
            || (int)statusCode >= 500;
    }

    private TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests
            && response.Headers.RetryAfter is { } retryAfter)
        {
            if (retryAfter.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            {
                return delta;
            }

            if (retryAfter.Date is DateTimeOffset retryAt)
            {
                var wait = retryAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    return wait;
                }
            }
        }

        return ComputeRetryDelay(attempt);
    }

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponentialBackoffFactor = 1 << Math.Min(attempt, 6);
        return TimeSpan.FromMilliseconds(_retryDelay.TotalMilliseconds * exponentialBackoffFactor);
    }

    private async Task<string?> TryReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        // Fast-path reject when server advertises a body larger than our safety cap.
        if (content.Headers.ContentLength is long length && length > _maxResponseBodyBytes)
        {
            return null;
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var totalBytesRead = 0;

        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            totalBytesRead += read;
            if (totalBytesRead > _maxResponseBodyBytes)
            {
                // Enforce limit even when Content-Length is missing or incorrect.
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}


