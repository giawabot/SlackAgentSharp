namespace SlackAgentSharp;

public sealed class SlackPlan
{
    private readonly SlackClient slackClient;
    private readonly string channelId;
    private readonly string threadTimestamp;
    private string? messageTimestamp;

    public SlackPlan(SlackClient slackClient, string channelId, string threadTimestamp)
    {
        this.slackClient = slackClient ?? throw new ArgumentNullException(nameof(slackClient));
        this.channelId = channelId ?? throw new ArgumentNullException(nameof(channelId));
        this.threadTimestamp = threadTimestamp ?? throw new ArgumentNullException(nameof(threadTimestamp));
    }

    public Task SendInitialAsync(SlackTaskPlan plan, CancellationToken token)
    {
        if (!string.IsNullOrWhiteSpace(messageTimestamp))
        {
            return Task.CompletedTask;
        }

        return SendAsync(plan, token, isUpdate: false);
    }

    public Task SendTaskUpdatesAsync(SlackTaskPlan plan, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(messageTimestamp))
        {
            return SendAsync(plan, token, isUpdate: false);
        }

        return SendAsync(plan, token, isUpdate: true);
    }

    private async Task SendAsync(SlackTaskPlan plan, CancellationToken token, bool isUpdate)
    {
        var blocks = BuildBlocks(plan);
        if (isUpdate && !string.IsNullOrWhiteSpace(messageTimestamp))
        {
            await slackClient.UpdateMessageBlocksAsync(channelId, messageTimestamp!, null, blocks, token);
            return;
        }

        messageTimestamp = await slackClient.SendMessageWithBlocksAsync(
            channelId,
            null,
            blocks,
            threadTimestamp,
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

