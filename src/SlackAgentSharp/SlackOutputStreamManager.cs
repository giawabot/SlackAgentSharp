using System.Text;

namespace SlackAgentSharp;

public sealed class SlackOutputStreamManager
{
    private const string ToolCallPrefix = "<tool_call";
    private readonly SlackClient slackClient;
    private readonly string channelId;
    private readonly string threadTimestamp;
    private readonly string recipientUserId;
    // Guards lazy stream-session creation so concurrent chunks don't start multiple Slack streams.
    private readonly SemaphoreSlim startSemaphore = new(1, 1);
    private SlackStreamSession? currentSession;
    private int hadOutput;
    private bool suppressBlock;
    private bool decisionMade;
    private bool startNotified;
    private StringBuilder? pendingBuffer;

    /// <summary>
    /// Creates a streaming output manager for a Slack thread.
    /// </summary>
    /// <param name="slackClient">Slack client used for stream operations.</param>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Slack thread timestamp.</param>
    /// <param name="recipientUserId">Optional recipient user ID.</param>
    public SlackOutputStreamManager(
        SlackClient slackClient,
        string channelId,
        string threadTimestamp,
        string recipientUserId)
    {
        this.slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        this.channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        this.threadTimestamp = threadTimestamp ?? throw new ArgumentNullException(nameof(threadTimestamp));
        this.recipientUserId = recipientUserId ?? string.Empty;
    }

    public bool HadOutput => Volatile.Read(ref hadOutput) == 1;

    /// <summary>
    /// Starts a new output block and resets streaming state.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockStarted(CancellationToken cancellationToken)
    {
        if (currentSession is not null)
        {
            await currentSession.StopAsync(null, cancellationToken);
            currentSession = null;
        }

        suppressBlock = false;
        decisionMade = false;
        startNotified = false;
        pendingBuffer = new StringBuilder();
    }

    /// <summary>
    /// Handles a streamed output chunk.
    /// </summary>
    /// <param name="chunk">Output text chunk.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task OnOutputChunk(string chunk, CancellationToken cancellationToken)
    {
        return OnOutputChunk(chunk, null, cancellationToken);
    }

    /// <summary>
    /// Handles a streamed output chunk and triggers an optional callback when output starts.
    /// </summary>
    /// <param name="chunk">Output text chunk.</param>
    /// <param name="outputStarted">Optional callback invoked when output starts.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputChunk(
        string chunk,
        Func<CancellationToken, Task>? outputStarted,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(chunk))
        {
            return;
        }

        if (suppressBlock)
        {
            return;
        }

        if (!decisionMade)
        {
            pendingBuffer ??= new StringBuilder();
            pendingBuffer.Append(chunk);
            // We defer until we have enough characters to know whether this block is tool metadata.
            var trimmed = pendingBuffer.ToString().TrimStart();
            if (trimmed.Length < ToolCallPrefix.Length)
            {
                return;
            }

            decisionMade = true;
            if (trimmed.StartsWith(ToolCallPrefix, StringComparison.Ordinal))
            {
                // Tool-call envelopes are internal protocol data and should not be mirrored to Slack.
                suppressBlock = true;
                pendingBuffer.Clear();
                return;
            }

            if (!startNotified && outputStarted is not null)
            {
                startNotified = true;
                await outputStarted(cancellationToken);
            }

            currentSession = await EnsureSessionAsync(cancellationToken);
            if (currentSession is not null)
            {
                Interlocked.Exchange(ref hadOutput, 1);
                await currentSession.AppendAsync(pendingBuffer.ToString(), cancellationToken);
            }

            pendingBuffer.Clear();
            return;
        }

        if (currentSession is null)
        {
            currentSession = await EnsureSessionAsync(cancellationToken);
        }

        if (currentSession is not null)
        {
            if (!startNotified && outputStarted is not null)
            {
                startNotified = true;
                await outputStarted(cancellationToken);
            }

            Interlocked.Exchange(ref hadOutput, 1);
            await currentSession.AppendAsync(chunk, cancellationToken);
        }
    }

    /// <summary>
    /// Ends the current output block and stops any active stream session.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockEnded(CancellationToken cancellationToken)
    {
        if (currentSession is null)
        {
            suppressBlock = false;
            decisionMade = false;
            pendingBuffer?.Clear();
            return;
        }

        await currentSession.StopAsync(null, cancellationToken);
        currentSession = null;
        suppressBlock = false;
        decisionMade = false;
        startNotified = false;
        pendingBuffer?.Clear();
    }

    /// <summary>
    /// Completes output streaming for the current block.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        return OnOutputBlockEnded(cancellationToken);
    }

    private async Task<SlackStreamSession?> EnsureSessionAsync(CancellationToken cancellationToken)
    {
        await startSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (currentSession is not null)
            {
                return currentSession;
            }

            currentSession = await SlackStreamSession.StartAsync(
                slackClient,
                channelId,
                threadTimestamp,
                recipientUserId,
                cancellationToken);
            return currentSession;
        }
        finally
        {
            startSemaphore.Release();
        }
    }
}

