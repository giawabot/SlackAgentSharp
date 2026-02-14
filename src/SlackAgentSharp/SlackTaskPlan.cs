namespace SlackAgentSharp;

public enum SlackTaskStatus
{
    Pending,
    InProgress,
    Complete,
    Error
}

public sealed record SlackTaskPlanItem(string Id, string Title, SlackTaskStatus Status);

public sealed record SlackTaskPlan(string Title, IReadOnlyList<SlackTaskPlanItem> Tasks);
