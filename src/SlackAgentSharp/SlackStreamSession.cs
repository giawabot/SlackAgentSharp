using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Channels;

namespace SlackAgentSharp;

internal sealed class SlackStreamSession
{
    private const int BufferThreshold = 256;
    private const int ChannelCapacity = 8;
    private readonly SlackClient _slackClient;
    private readonly string _channelId;
    private readonly string _streamTimestamp;
    private readonly StringBuilder _pendingBuffer = new();
    private readonly Channel<string> _channel;
    private readonly Task _consumerTask;
    private readonly CancellationTokenSource _consumerCancellation = new();
    private readonly object _bufferLock = new();
    private Exception? _consumerException;
    private volatile bool _consumerFailed;

    private SlackStreamSession(SlackClient slackClient, string channelId, string streamTimestamp)
    {
        _slackClient = slackClient;
        _channelId = channelId;
        _streamTimestamp = streamTimestamp;
        _channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            // Backpressure producer writes instead of unbounded buffering.
            FullMode = BoundedChannelFullMode.Wait
        });
        _consumerTask = Task.Run(() => ConsumeAsync(_consumerCancellation.Token));
    }

    public static async Task<SlackStreamSession?> StartAsync(
        SlackClient slackClient,
        string channelId,
        string threadTimestamp,
        string recipientUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(slackClient);

        var streamTimestamp = await slackClient.StartMessageStreamAsync(
            channelId,
            threadTimestamp,
            markdownText: null,
            recipientTeamId: null,
            recipientUserId: string.IsNullOrWhiteSpace(recipientUserId) ? null : recipientUserId,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(streamTimestamp))
        {
            return null;
        }

        return new SlackStreamSession(slackClient, channelId, streamTimestamp);
    }

    public Task AppendAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfConsumerFailed();

        if (string.IsNullOrEmpty(text))
        {
            return Task.CompletedTask;
        }

        string? payload = null;
        lock (_bufferLock)
        {
            _pendingBuffer.Append(text);
            if (_pendingBuffer.Length < BufferThreshold)
            {
                return Task.CompletedTask;
            }

            payload = _pendingBuffer.ToString();
            _pendingBuffer.Clear();
        }

        return WritePayloadAsync(payload, cancellationToken);
    }

    public async Task StopAsync(string? finalMarkdownText, CancellationToken cancellationToken)
    {
        string? finalPayload = null;
        lock (_bufferLock)
        {
            if (!string.IsNullOrWhiteSpace(finalMarkdownText))
            {
                _pendingBuffer.Append(finalMarkdownText);
            }

            if (_pendingBuffer.Length > 0)
            {
                finalPayload = _pendingBuffer.ToString();
                _pendingBuffer.Clear();
            }
        }

        if (!_consumerFailed && !string.IsNullOrEmpty(finalPayload))
        {
            await _channel.Writer.WriteAsync(finalPayload, cancellationToken);
        }

        _channel.Writer.TryComplete();
        // If caller cancels shutdown, also cancel the consumer so await _consumerTask can unwind.
        using var cancellationRegistration = cancellationToken.Register(() => _consumerCancellation.Cancel());
        try
        {
            await _consumerTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        var stopped = await _slackClient.StopMessageStreamAsync(_channelId, _streamTimestamp, markdownText: null, cancellationToken);
        _consumerCancellation.Dispose();

        if (!stopped)
        {
            throw new InvalidOperationException("Slack rejected the stream stop request.");
        }

        ThrowIfConsumerFailed();
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (string.IsNullOrEmpty(payload))
                {
                    continue;
                }

                var appended = await _slackClient.AppendMessageStreamAsync(_channelId, _streamTimestamp, payload, cancellationToken);
                if (!appended)
                {
                    throw new InvalidOperationException("Slack rejected a stream append request.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _consumerException = exception;
            _consumerFailed = true;
        }
    }

    private void ThrowIfConsumerFailed()
    {
        if (!_consumerFailed)
        {
            return;
        }

        var exception = _consumerException ?? new InvalidOperationException("Slack stream consumer failed.");
        ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private async Task WritePayloadAsync(string payload, CancellationToken cancellationToken)
    {
        ThrowIfConsumerFailed();
        await _channel.Writer.WriteAsync(payload, cancellationToken);
    }
}
