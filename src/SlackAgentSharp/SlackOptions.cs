namespace SlackAgentSharp;

public sealed class SlackOptions
{
    public string BotToken { get; set; } = "";

    public string UserId { get; set; } = "";

    public int PollingDelay { get; set; } = 5;

    public bool IncludeReasoning { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 30;

    public int TransientRetryCount { get; set; } = 2;

    public int RetryDelayMilliseconds { get; set; } = 250;
}

