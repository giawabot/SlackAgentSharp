namespace SlackAgentSharp;

public sealed class SlackOptions
{
    public string BotToken { get; set; } = "";

    public string UserId { get; set; } = "";

    public int PollingDelay { get; set; } = 5;

    public bool IncludeReasoning { get; set; }
}

