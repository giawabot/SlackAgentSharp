using System.Text;

namespace SlackAgentSharp;

public sealed class SlackOutputStreamManager
{
    private readonly SlackClient _slackClient;
    private readonly IOutputChunkFilter _outputChunkFilter;
    private readonly string _channelId;
    private readonly string _threadTimestamp;
    private readonly string _recipientUserId;
    // Serializes state transitions across block/chunk lifecycle methods.
    private readonly SemaphoreSlim _stateSemaphore = new(1, 1);
    // Guards lazy stream-session creation so concurrent chunks don't start multiple Slack streams.
    private readonly SemaphoreSlim _startSemaphore = new(1, 1);
    private SlackStreamSession? _currentSession;
    private int _hadOutput;
    private bool _suppressBlock;
    private bool _decisionMade;
    private bool _startNotified;
    private long _blockGeneration;
    private StringBuilder? _pendingBuffer;

    /// <summary>
    /// Creates a streaming output manager for a Slack thread.
    /// </summary>
    /// <param name="slackClient">Slack client used for stream operations.</param>
    /// <param name="options">Slack options containing the configured output filter.</param>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Slack thread timestamp.</param>
    /// <param name="recipientUserId">Optional recipient user ID.</param>
    public SlackOutputStreamManager(
        SlackClient slackClient,
        SlackOptions options,
        string channelId,
        string threadTimestamp,
        string recipientUserId)
        : this(
            slackClient,
            (options ?? throw new ArgumentNullException(nameof(options))).OutputChunkFilter,
            channelId,
            threadTimestamp,
            recipientUserId)
    {
    }

    /// <summary>
    /// Creates a streaming output manager for a Slack thread.
    /// </summary>
    /// <param name="slackClient">Slack client used for stream operations.</param>
    /// <param name="outputChunkFilter">Filter used to suppress non-user-facing output.</param>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Slack thread timestamp.</param>
    /// <param name="recipientUserId">Optional recipient user ID.</param>
    public SlackOutputStreamManager(
        SlackClient slackClient,
        IOutputChunkFilter outputChunkFilter,
        string channelId,
        string threadTimestamp,
        string recipientUserId)
    {
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _outputChunkFilter = outputChunkFilter ?? throw new ArgumentNullException(nameof(outputChunkFilter));
        _channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _threadTimestamp = threadTimestamp ?? throw new ArgumentNullException(nameof(threadTimestamp));
        _recipientUserId = recipientUserId ?? string.Empty;
    }

    public bool HadOutput => Volatile.Read(ref _hadOutput) == 1;

    /// <summary>
    /// Starts a new output block and resets streaming state.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockStarted(CancellationToken cancellationToken)
    {
        SlackStreamSession? sessionToStop;
        await _stateSemaphore.WaitAsync(cancellationToken);
        try
        {
            _blockGeneration++;
            sessionToStop = _currentSession;
            _currentSession = null;
            _suppressBlock = false;
            _decisionMade = false;
            _startNotified = false;
            _pendingBuffer = new StringBuilder();
        }
        finally
        {
            _stateSemaphore.Release();
        }

        if (sessionToStop is not null)
        {
            await sessionToStop.StopAsync(null, cancellationToken);
        }
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
        string? payload = null;
        var notifyStart = false;
        SlackStreamSession? session;
        long generation;

        await _stateSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (string.IsNullOrEmpty(chunk) || _suppressBlock)
            {
                return;
            }

            generation = _blockGeneration;
            if (!_decisionMade)
            {
                _pendingBuffer ??= new StringBuilder();
                _pendingBuffer.Append(chunk);

                var decision = _outputChunkFilter.EvaluateBufferedOutput(_pendingBuffer.ToString());
                if (decision == OutputChunkFilterDecision.Undecided)
                {
                    return;
                }

                _decisionMade = true;
                if (decision == OutputChunkFilterDecision.Suppress)
                {
                    _suppressBlock = true;
                    _pendingBuffer.Clear();
                    return;
                }

                payload = _pendingBuffer.ToString();
                _pendingBuffer.Clear();
            }
            else
            {
                payload = chunk;
            }

            if (!_startNotified && outputStarted is not null)
            {
                _startNotified = true;
                notifyStart = true;
            }

            session = _currentSession;
        }
        finally
        {
            _stateSemaphore.Release();
        }

        if (notifyStart && outputStarted is not null)
        {
            await outputStarted(cancellationToken);
        }

        if (string.IsNullOrEmpty(payload))
        {
            return;
        }

        if (session is null)
        {
            session = await EnsureSessionAsync(generation, cancellationToken);
            if (session is null)
            {
                return;
            }
        }

        await _stateSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_blockGeneration != generation || _suppressBlock || !ReferenceEquals(_currentSession, session))
            {
                return;
            }

            Interlocked.Exchange(ref _hadOutput, 1);
        }
        finally
        {
            _stateSemaphore.Release();
        }

        await session.AppendAsync(payload, cancellationToken);
    }

    /// <summary>
    /// Ends the current output block and stops any active stream session.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockEnded(CancellationToken cancellationToken)
    {
        SlackStreamSession? sessionToStop;

        await _stateSemaphore.WaitAsync(cancellationToken);
        try
        {
            _blockGeneration++;
            sessionToStop = _currentSession;
            _currentSession = null;
            _suppressBlock = false;
            _decisionMade = false;
            _startNotified = false;
            _pendingBuffer?.Clear();
        }
        finally
        {
            _stateSemaphore.Release();
        }

        if (sessionToStop is not null)
        {
            await sessionToStop.StopAsync(null, cancellationToken);
        }
    }

    /// <summary>
    /// Completes output streaming for the current block.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public Task CompleteAsync(CancellationToken cancellationToken)
    {
        return OnOutputBlockEnded(cancellationToken);
    }

    private async Task<SlackStreamSession?> EnsureSessionAsync(long expectedGeneration, CancellationToken cancellationToken)
    {
        SlackStreamSession? createdSession = null;
        SlackStreamSession? resolvedSession = null;
        var createdSessionAdopted = false;
        var shouldCreateSession = false;

        await _startSemaphore.WaitAsync(cancellationToken);
        try
        {
            await _stateSemaphore.WaitAsync(cancellationToken);
            try
            {
                if (_blockGeneration == expectedGeneration && !_suppressBlock)
                {
                    if (_currentSession is not null)
                    {
                        resolvedSession = _currentSession;
                    }
                    else
                    {
                        shouldCreateSession = true;
                    }
                }
            }
            finally
            {
                _stateSemaphore.Release();
            }

            if (resolvedSession is null && shouldCreateSession)
            {
                createdSession = await SlackStreamSession.StartAsync(
                    _slackClient,
                    _channelId,
                    _threadTimestamp,
                    _recipientUserId,
                    cancellationToken);
                if (createdSession is null)
                {
                    return null;
                }

                await _stateSemaphore.WaitAsync(cancellationToken);
                try
                {
                    if (_blockGeneration == expectedGeneration && !_suppressBlock)
                    {
                        if (_currentSession is null)
                        {
                            _currentSession = createdSession;
                            createdSessionAdopted = true;
                            resolvedSession = createdSession;
                        }
                        else
                        {
                            resolvedSession = _currentSession;
                        }
                    }

                }
                finally
                {
                    _stateSemaphore.Release();
                }
            }
        }
        finally
        {
            _startSemaphore.Release();
        }

        if (createdSession is not null && !createdSessionAdopted)
        {
            await createdSession.StopAsync(null, cancellationToken);
        }

        return resolvedSession;
    }
}
