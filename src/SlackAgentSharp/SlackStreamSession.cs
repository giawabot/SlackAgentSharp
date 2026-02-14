using System.Text;
using System.Threading.Channels;

namespace SlackAgentSharp;

internal sealed class SlackStreamSession
{
    private const int BufferThreshold = 256;
    private const int ChannelCapacity = 8;
    private readonly SlackClient slackClient;
    private readonly string channelId;
    private readonly string streamTimestamp;
    private readonly StringBuilder pendingBuffer = new();
    private readonly Channel<string> channel;
    private readonly Task consumerTask;
    private readonly CancellationTokenSource consumerCancellation = new();
    private readonly object bufferLock = new();
    private Exception? consumerException;
    private volatile bool consumerFailed;

    private SlackStreamSession(SlackClient slackClient, string channelId, string streamTimestamp)
    {
        this.slackClient = slackClient;
        this.channelId = channelId;
        this.streamTimestamp = streamTimestamp;
        channel = Channel.CreateBounded<string>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        consumerTask = Task.Run(() => ConsumeAsync(consumerCancellation.Token));
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
        if (string.IsNullOrEmpty(text) || consumerFailed)
        {
            return Task.CompletedTask;
        }

        lock (bufferLock)
        {
            pendingBuffer.Append(text);
            if (pendingBuffer.Length < BufferThreshold)
            {
                return Task.CompletedTask;
            }

            var payload = pendingBuffer.ToString();
            if (channel.Writer.TryWrite(payload))
            {
                pendingBuffer.Clear();
            }
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(string? finalMarkdownText, CancellationToken cancellationToken)
    {
        string? finalPayload = null;
        lock (bufferLock)
        {
            if (!string.IsNullOrWhiteSpace(finalMarkdownText))
            {
                pendingBuffer.Append(finalMarkdownText);
            }

            if (pendingBuffer.Length > 0)
            {
                finalPayload = pendingBuffer.ToString();
                pendingBuffer.Clear();
            }
        }

        if (!consumerFailed && !string.IsNullOrEmpty(finalPayload))
        {
            await channel.Writer.WriteAsync(finalPayload, cancellationToken);
        }

        channel.Writer.TryComplete();
        using var cancellationRegistration = cancellationToken.Register(() => consumerCancellation.Cancel());
        try
        {
            await consumerTask;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }

        if (consumerException is not null)
        {
            Console.WriteLine($"Slack stream consumer failed: {consumerException.Message}");
        }

        await slackClient.StopMessageStreamAsync(channelId, streamTimestamp, markdownText: null, cancellationToken);
        consumerCancellation.Dispose();
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(cancellationToken))
            {
                if (string.IsNullOrEmpty(payload))
                {
                    continue;
                }

                await slackClient.AppendMessageStreamAsync(channelId, streamTimestamp, payload, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            consumerException = exception;
            consumerFailed = true;
        }
    }
}

