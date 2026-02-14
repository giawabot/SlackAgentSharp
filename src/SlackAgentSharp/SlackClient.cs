using System.Net.Http.Headers;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlackAgentSharp;

public sealed class SlackClient : IDisposable
{
    private const string SlackApiBaseUrl = "https://slack.com/api/";
    private readonly HttpClient httpClient;
    private readonly JsonSerializerOptions serializerOptions;
    private readonly TimeSpan requestTimeout;
    private readonly int transientRetryCount;
    private readonly TimeSpan retryDelay;
    private readonly int maxResponseBodyBytes;
    private bool disposed;

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

        requestTimeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
        transientRetryCount = options.TransientRetryCount;
        retryDelay = TimeSpan.FromMilliseconds(options.RetryDelayMilliseconds);
        maxResponseBodyBytes = options.MaxResponseBodyBytes;

        httpClient = new HttpClient
        {
            BaseAddress = new Uri(SlackApiBaseUrl, UriKind.Absolute),
            // Per-request CTS timeouts are enforced in SendWithRetryAsync for retry-aware timing.
            Timeout = Timeout.InfiniteTimeSpan
        };
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", options.BotToken);

        serializerOptions = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            PropertyNameCaseInsensitive = true,
        };
    }

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

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    public async Task<string?> OpenDirectMessageChannelAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new SlackConversationOpenRequest(userId), serializerOptions);
        using var response = await SendPostAsync("conversations.open", payload, cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var openResponse = JsonSerializer.Deserialize<SlackConversationOpenResponse>(responseBody, serializerOptions);
        if (openResponse is null || !openResponse.Ok)
        {
            return null;
        }

        return openResponse.Channel?.Id;
    }

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

        var historyResponse = JsonSerializer.Deserialize<SlackConversationHistoryResponse>(responseBody, serializerOptions);
        if (historyResponse is null || !historyResponse.Ok)
        {
            return [];
        }

        return historyResponse.Messages ?? [];
    }

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

        var repliesResponse = JsonSerializer.Deserialize<SlackConversationHistoryResponse>(responseBody, serializerOptions);
        if (repliesResponse is null || !repliesResponse.Ok)
        {
            return [];
        }

        return repliesResponse.Messages ?? [];
    }

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

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

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
            serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

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
            serializerOptions);
        var response = await SendAssistantInternalAsync("assistant.threads.setStatus", payload, cancellationToken);
        return response.Ok;
    }

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
            serializerOptions);
        var response = await SendStreamInternalAsync("chat.startStream", payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

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
            serializerOptions);
        var response = await SendStreamInternalAsync("chat.appendStream", payload, cancellationToken);
        return response.Ok;
    }

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
            serializerOptions);
        var response = await SendStreamInternalAsync("chat.stopStream", payload, cancellationToken);
        return response.Ok;
    }

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

        var payload = JsonSerializer.Serialize(new SlackMessageRequest(channelId, message), serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

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
            serializerOptions);
        var response = await SendMessageInternalAsync(payload, cancellationToken);
        return response.Ok ? response.Timestamp : null;
    }

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
            serializerOptions);
        var response = await SendChatUpdateInternalAsync(payload, cancellationToken);
        return response.Ok;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        httpClient.Dispose();
        disposed = true;
    }

    private async Task<SlackApiResponse> SendMessageInternalAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync("chat.postMessage", payload, cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, "http_error");
        }

        var messageResponse = JsonSerializer.Deserialize<SlackApiResponse>(responseBody, serializerOptions);
        if (messageResponse is null || !messageResponse.Ok)
        {
            return new SlackApiResponse(false, null, messageResponse?.Error);
        }

        return messageResponse;
    }

    private async Task<SlackApiResponse> SendStreamInternalAsync(
        string endpoint,
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(endpoint, payload, cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, "http_error");
        }

        var streamResponse = JsonSerializer.Deserialize<SlackApiResponse>(responseBody, serializerOptions);
        if (streamResponse is null || !streamResponse.Ok)
        {
            return new SlackApiResponse(false, null, streamResponse?.Error);
        }

        return streamResponse;
    }

    private async Task<SlackApiResponse> SendChatUpdateInternalAsync(
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync("chat.update", payload, cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, "http_error");
        }

        var updateResponse = JsonSerializer.Deserialize<SlackApiResponse>(responseBody, serializerOptions);
        if (updateResponse is null || !updateResponse.Ok)
        {
            return new SlackApiResponse(false, null, updateResponse?.Error);
        }

        return updateResponse;
    }

    private async Task<SlackApiResponse> SendAssistantInternalAsync(
        string endpoint,
        string payload,
        CancellationToken cancellationToken)
    {
        using var response = await SendPostAsync(endpoint, payload, cancellationToken);
        var responseBody = await TryReadResponseBodyAsync(response.Content, cancellationToken);
        if (responseBody is null)
        {
            return new SlackApiResponse(false, null, "response_too_large");
        }

        if (!response.IsSuccessStatusCode)
        {
            return new SlackApiResponse(false, null, "http_error");
        }

        var statusResponse = JsonSerializer.Deserialize<SlackApiResponse>(responseBody, serializerOptions);
        if (statusResponse is null || !statusResponse.Ok)
        {
            return new SlackApiResponse(false, null, statusResponse?.Error);
        }

        return statusResponse;
    }

    private Task<HttpResponseMessage> SendGetAsync(string requestUri, CancellationToken cancellationToken)
    {
        return SendWithRetryAsync(
            () => new HttpRequestMessage(HttpMethod.Get, requestUri),
            cancellationToken);
    }

    private Task<HttpResponseMessage> SendPostAsync(string endpoint, string payload, CancellationToken cancellationToken)
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
            cancellationToken);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt <= transientRetryCount; attempt++)
        {
            using var request = requestFactory();
            using var timeoutToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutToken.CancelAfter(requestTimeout);
            try
            {
                var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeoutToken.Token);

                if (attempt < transientRetryCount && IsTransientStatusCode(response.StatusCode))
                {
                    response.Dispose();
                    await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
                    continue;
                }

                return response;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < transientRetryCount)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
            catch (HttpRequestException) when (attempt < transientRetryCount)
            {
                await Task.Delay(ComputeRetryDelay(attempt), cancellationToken);
            }
        }

        throw new InvalidOperationException("Retry loop exited unexpectedly.");
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

    private TimeSpan ComputeRetryDelay(int attempt)
    {
        var exponentialBackoffFactor = 1 << Math.Min(attempt, 6);
        return TimeSpan.FromMilliseconds(retryDelay.TotalMilliseconds * exponentialBackoffFactor);
    }

    private async Task<string?> TryReadResponseBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maxResponseBodyBytes)
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
            if (totalBytesRead > maxResponseBodyBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}

