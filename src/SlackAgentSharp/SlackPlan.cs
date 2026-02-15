namespace SlackAgentSharp;

public sealed class SlackPlan
{
    private readonly SlackClient _slackClient;
    private readonly string _channelId;
    private readonly string _threadTimestamp;
    private string? _messageTimestamp;

    /// <summary>
    /// Creates a Slack plan publisher for a channel thread.
    /// </summary>
    /// <param name="slackClient">Slack client used for message operations.</param>
    /// <param name="channelId">Slack channel ID.</param>
    /// <param name="threadTimestamp">Parent thread timestamp.</param>
    public SlackPlan(SlackClient slackClient, string channelId, string threadTimestamp)
    {
        _slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        _channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        _threadTimestamp = threadTimestamp ?? throw new ArgumentNullException(nameof(threadTimestamp));
    }

    /// <summary>
    /// Sends the initial plan message if one has not already been sent.
    /// </summary>
    /// <param name="plan">Plan payload to publish.</param>
    /// <param name="token">Token used to cancel the operation.</param>
    public Task SendInitialAsync(SlackTaskPlan plan, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(_messageTimestamp))
        {
            return Task.CompletedTask;
        }

        return SendAsync(plan, token, isUpdate: false);
    }

    /// <summary>
    /// Sends or updates the plan message with current task states.
    /// </summary>
    /// <param name="plan">Plan payload to publish.</param>
    /// <param name="token">Token used to cancel the operation.</param>
    public Task SendTaskUpdatesAsync(SlackTaskPlan plan, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_messageTimestamp))
        {
            return SendAsync(plan, token, isUpdate: false);
        }

        return SendAsync(plan, token, isUpdate: true);
    }

    private async Task SendAsync(SlackTaskPlan plan, CancellationToken token, bool isUpdate)
    {
        var blocks = BuildBlocks(plan);
        if (isUpdate && !string.IsNullOrWhiteSpace(_messageTimestamp))
        {
            var updated = await _slackClient.UpdateMessageBlocksAsync(_channelId, _messageTimestamp!, null, blocks, token);
            if (updated)
            {
                return;
            }

            // Recover if the prior message was deleted or is otherwise no longer updatable.
            _messageTimestamp = null;
        }

        _messageTimestamp = await _slackClient.SendMessageWithBlocksAsync(
            _channelId,
            null,
            blocks,
            _threadTimestamp,
            token);
    }

    private static string ToSlackStatus(SlackTaskStatus status) =>
        status switch
        {
            SlackTaskStatus.Pending => "pending",
            SlackTaskStatus.InProgress => "in_progress",
            SlackTaskStatus.Complete => "complete",
            SlackTaskStatus.Error => "error",
            _ => "pending"
        };

    private static List<PlanTaskBlock> BuildPlanTasks(IEnumerable<SlackTaskPlanItem> tasks)
    {
        var blocks = new List<PlanTaskBlock>();
        foreach (var task in tasks)
        {
            blocks.Add(new PlanTaskBlock(task.Id, task.Title, ToSlackStatus(task.Status)));
        }

        return blocks;
    }

    private static List<object> BuildBlocks(SlackTaskPlan plan)
    {
        return
        [
            new PlanBlock(plan.Title, BuildPlanTasks(plan.Tasks))
        ];
    }
}


