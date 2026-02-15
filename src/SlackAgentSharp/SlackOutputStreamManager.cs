using System.Text;
using System.Threading.Channels;

namespace SlackAgentSharp;

public sealed class SlackOutputStreamManager
{
    private readonly SlackClient _slackClient;
    private readonly IOutputChunkFilter _outputChunkFilter;
    private readonly string _channelId;
    private readonly string _threadTimestamp;
    private readonly string _recipientUserId;
    // Run all lifecycle/chunk operations on one worker to avoid lock+await race patterns.
    private readonly Channel<WorkItem> _workChannel = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly Task _workLoop;
    private SlackStreamSession? _currentSession;
    private int _hadOutput;
    private bool _suppressBlock;
    private bool _decisionMade;
    private bool _startNotified;
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
        _workLoop = Task.Run(ProcessWorkItemsAsync);
    }

    public bool HadOutput => Volatile.Read(ref _hadOutput) == 1;

    /// <summary>
    /// Starts a new output block and resets streaming state.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockStarted(CancellationToken cancellationToken)
    {
        await EnqueueAsync(
            async token =>
            {
                if (_currentSession is not null)
                {
                    await _currentSession.StopAsync(null, token);
                    _currentSession = null;
                }

                _suppressBlock = false;
                _decisionMade = false;
                _startNotified = false;
                _pendingBuffer = new StringBuilder();
            },
            cancellationToken);
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
        await EnqueueAsync(
            async token =>
            {
                if (string.IsNullOrEmpty(chunk) || _suppressBlock)
                {
                    return;
                }

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

                    if (!_startNotified && outputStarted is not null)
                    {
                        _startNotified = true;
                        await outputStarted(token);
                    }

                    _currentSession = await EnsureSessionAsync(token);
                    if (_currentSession is not null)
                    {
                        Interlocked.Exchange(ref _hadOutput, 1);
                        await _currentSession.AppendAsync(_pendingBuffer.ToString(), token);
                    }

                    _pendingBuffer.Clear();
                    return;
                }

                if (_currentSession is null)
                {
                    _currentSession = await EnsureSessionAsync(token);
                }

                if (_currentSession is not null)
                {
                    if (!_startNotified && outputStarted is not null)
                    {
                        _startNotified = true;
                        await outputStarted(token);
                    }

                    Interlocked.Exchange(ref _hadOutput, 1);
                    await _currentSession.AppendAsync(chunk, token);
                }
            },
            cancellationToken);
    }

    /// <summary>
    /// Ends the current output block and stops any active stream session.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    public async Task OnOutputBlockEnded(CancellationToken cancellationToken)
    {
        await EnqueueAsync(
            async token =>
            {
                if (_currentSession is not null)
                {
                    await _currentSession.StopAsync(null, token);
                    _currentSession = null;
                }

                _suppressBlock = false;
                _decisionMade = false;
                _startNotified = false;
                _pendingBuffer?.Clear();
            },
            cancellationToken);
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
        if (_currentSession is not null)
        {
            return _currentSession;
        }

        _currentSession = await SlackStreamSession.StartAsync(
            _slackClient,
            _channelId,
            _threadTimestamp,
            _recipientUserId,
            cancellationToken);
        return _currentSession;
    }

    private async Task EnqueueAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        var workItem = new WorkItem(operation, cancellationToken);
        await _workChannel.Writer.WriteAsync(workItem, cancellationToken);
        await workItem.Completion.Task.WaitAsync(cancellationToken);
    }

    private async Task ProcessWorkItemsAsync()
    {
        await foreach (var workItem in _workChannel.Reader.ReadAllAsync())
        {
            try
            {
                await workItem.Operation(workItem.CancellationToken);
                workItem.Completion.TrySetResult();
            }
            catch (OperationCanceledException) when (workItem.CancellationToken.IsCancellationRequested)
            {
                workItem.Completion.TrySetCanceled(workItem.CancellationToken);
            }
            catch (Exception exception)
            {
                workItem.Completion.TrySetException(exception);
            }
        }
    }

    private sealed class WorkItem
    {
        public WorkItem(Func<CancellationToken, Task> operation, CancellationToken cancellationToken)
        {
            Operation = operation;
            CancellationToken = cancellationToken;
            Completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public Func<CancellationToken, Task> Operation { get; }
        public CancellationToken CancellationToken { get; }

        public TaskCompletionSource Completion { get; }
    }
}
