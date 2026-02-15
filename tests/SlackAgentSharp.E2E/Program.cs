using System.Globalization;
using System.Text;
using System.Text.Json;
using SlackAgentSharp;

var config = HarnessConfiguration.Load();
var settings = config.Slack;

var slackOptions = new SlackOptions
{
    BotToken = settings.BotToken,
    UserId = settings.UserId,
    PollingDelay = settings.PollingDelaySeconds,
    IncludeReasoning = true,
    OutputChunkFilter = AllowAllOutputChunkFilter.Instance
};

using var slackClient = new SlackClient(slackOptions);
using var cancellationSource = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) =>
{
    args.Cancel = true;
    cancellationSource.Cancel();
};

var pollDelay = TimeSpan.FromSeconds(Math.Max(1, settings.PollingDelaySeconds));
var processingDelay = TimeSpan.FromMilliseconds(Math.Max(0, settings.SimulatedProcessingDelayMilliseconds));
var chunkDelay = TimeSpan.FromMilliseconds(Math.Max(0, settings.StreamChunkDelayMilliseconds));
var chunkSize = Math.Max(8, settings.StreamChunkSize);
var recipientUserId = string.IsNullOrWhiteSpace(settings.RecipientUserId) ? settings.UserId : settings.RecipientUserId;
var lastSeenTimestamp = string.IsNullOrWhiteSpace(settings.ListenFromTimestamp)
    ? settings.ThreadTimestamp
    : settings.ListenFromTimestamp;

var planPublisher = new SlackPlan(slackClient, settings.ChannelId, settings.ThreadTimestamp);
var streamManager = new SlackOutputStreamManager(
    slackClient,
    AllowAllOutputChunkFilter.Instance,
    settings.ChannelId,
    settings.ThreadTimestamp,
    recipientUserId);

WriteLine("E2E harness started.");
WriteLine($"Watching channel {settings.ChannelId} thread {settings.ThreadTimestamp} for user {settings.UserId}.");

while (!cancellationSource.IsCancellationRequested)
{
    try
    {
        var replies = await slackClient.GetConversationRepliesAsync(
            settings.ChannelId,
            settings.ThreadTimestamp,
            lastSeenTimestamp,
            cancellationSource.Token);

        var pendingMessages = replies
            .Where(message => IsUserMessage(message, settings.UserId))
            .Where(message => IsStrictlyAfter(message.Timestamp, lastSeenTimestamp))
            .OrderBy(message => ParseTimestamp(message.Timestamp!))
            .ToList();

        foreach (var message in pendingMessages)
        {
            if (message.Timestamp is null)
            {
                continue;
            }

            lastSeenTimestamp = message.Timestamp;
            await ProcessMessageAsync(
                slackClient,
                planPublisher,
                streamManager,
                settings,
                message,
                processingDelay,
                chunkDelay,
                chunkSize,
                cancellationSource.Token);
        }
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
        break;
    }
    catch (Exception exception)
    {
        WriteLine($"Harness loop error: {exception.Message}");
    }

    try
    {
        await Task.Delay(pollDelay, cancellationSource.Token);
    }
    catch (OperationCanceledException) when (cancellationSource.IsCancellationRequested)
    {
        break;
    }
}

WriteLine("E2E harness stopped.");

static async Task ProcessMessageAsync(
    SlackClient slackClient,
    SlackPlan planPublisher,
    SlackOutputStreamManager streamManager,
    SlackHarnessSettings settings,
    SlackMessage message,
    TimeSpan processingDelay,
    TimeSpan chunkDelay,
    int chunkSize,
    CancellationToken cancellationToken)
{
    var messageText = string.IsNullOrWhiteSpace(message.Text) ? "(empty message)" : message.Text.Trim();
    WriteLine($"Processing message {message.Timestamp}: {messageText}");

    var tasks = new List<SlackTaskPlanItem>
    {
        new("read_context", "Read latest thread context", SlackTaskStatus.Pending),
        new("draft", "Draft assistant response", SlackTaskStatus.Pending),
        new("stream", "Stream response to thread", SlackTaskStatus.Pending),
        new("metadata", "Update title and prompt suggestions", SlackTaskStatus.Pending)
    };

    await planPublisher.SendInitialAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);
    await EnsureTrue(
        slackClient.SetAssistantThreadStatusAsync(
            settings.ChannelId,
            settings.ThreadTimestamp,
            settings.ThinkingStatus,
            cancellationToken),
        "assistant.threads.setStatus(thinking)");

    tasks = UpdateTaskStatus(tasks, "read_context", SlackTaskStatus.InProgress);
    await planPublisher.SendTaskUpdatesAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);
    await Task.Delay(processingDelay, cancellationToken);

    tasks = UpdateTaskStatus(tasks, "read_context", SlackTaskStatus.Complete);
    tasks = UpdateTaskStatus(tasks, "draft", SlackTaskStatus.InProgress);
    await planPublisher.SendTaskUpdatesAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);

    var response = BuildMockAssistantResponse(messageText);
    await Task.Delay(processingDelay, cancellationToken);

    tasks = UpdateTaskStatus(tasks, "draft", SlackTaskStatus.Complete);
    tasks = UpdateTaskStatus(tasks, "stream", SlackTaskStatus.InProgress);
    await planPublisher.SendTaskUpdatesAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);

    await streamManager.OnOutputBlockStarted(cancellationToken);
    foreach (var chunk in Chunk(response, chunkSize))
    {
        await streamManager.OnOutputChunk(chunk, cancellationToken);
        if (chunkDelay > TimeSpan.Zero)
        {
            await Task.Delay(chunkDelay, cancellationToken);
        }
    }

    await streamManager.OnOutputBlockEnded(cancellationToken);

    tasks = UpdateTaskStatus(tasks, "stream", SlackTaskStatus.Complete);
    tasks = UpdateTaskStatus(tasks, "metadata", SlackTaskStatus.InProgress);
    await planPublisher.SendTaskUpdatesAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);

    var threadTitle = BuildThreadTitle(messageText);
    await EnsureTrue(
        slackClient.SetAssistantThreadTitleAsync(
            settings.ChannelId,
            settings.ThreadTimestamp,
            threadTitle,
            cancellationToken),
        "assistant.threads.setTitle");

    var prompts = new List<SlackSuggestedPrompt>
    {
        new("Ask for a shorter answer", "Can you summarize this in one short paragraph?"),
        new("Ask for action items", "List concrete action items from this response."),
        new("Ask for alternatives", "Give me two alternative approaches for this problem.")
    };

    await EnsureTrue(
        slackClient.SetAssistantThreadSuggestedPromptsAsync(
            settings.ChannelId,
            settings.ThreadTimestamp,
            settings.SuggestedPromptsTitle,
            prompts,
            cancellationToken),
        "assistant.threads.setSuggestedPrompts");

    await EnsureTrue(
        slackClient.SetAssistantThreadStatusAsync(
            settings.ChannelId,
            settings.ThreadTimestamp,
            settings.CompletedStatus,
            cancellationToken),
        "assistant.threads.setStatus(completed)");

    tasks = UpdateTaskStatus(tasks, "metadata", SlackTaskStatus.Complete);
    await planPublisher.SendTaskUpdatesAsync(new SlackTaskPlan("SlackAgentSharp E2E workflow", tasks), cancellationToken);

    WriteLine($"Completed message {message.Timestamp}.");
}

static async Task EnsureTrue(Task<bool> call, string operation)
{
    var ok = await call;
    if (!ok)
    {
        throw new InvalidOperationException($"Slack operation failed: {operation}");
    }
}

static bool IsUserMessage(SlackMessage message, string expectedUserId)
{
    if (string.IsNullOrWhiteSpace(message.Timestamp))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(message.BotId))
    {
        return false;
    }

    if (!string.IsNullOrWhiteSpace(message.Subtype))
    {
        return false;
    }

    return string.Equals(message.User, expectedUserId, StringComparison.Ordinal);
}

static bool IsStrictlyAfter(string? timestamp, string boundary)
{
    if (string.IsNullOrWhiteSpace(timestamp))
    {
        return false;
    }

    return ParseTimestamp(timestamp) > ParseTimestamp(boundary);
}

static decimal ParseTimestamp(string timestamp)
{
    return decimal.TryParse(timestamp, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
        ? value
        : 0m;
}

static string BuildMockAssistantResponse(string message)
{
    var builder = new StringBuilder();
    builder.AppendLine("Thanks for the message. This is an end-to-end SlackAgentSharp verification response.");
    builder.AppendLine();
    builder.AppendLine("What I saw:");
    builder.AppendLine($"- Latest user input: \"{message}\"");
    builder.AppendLine("- Plan/status/streaming/title/prompts are exercised in this run.");
    builder.AppendLine();
    builder.AppendLine("Next steps:");
    builder.AppendLine("1. Send another thread reply to re-run the flow.");
    builder.AppendLine("2. Change delays/chunk size in appsettings.json to simulate different model behaviors.");
    return builder.ToString().TrimEnd();
}

static string BuildThreadTitle(string message)
{
    var words = message
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Take(7)
        .ToArray();

    var title = words.Length == 0
        ? "SlackAgentSharp E2E session"
        : string.Join(' ', words);

    if (title.Length > 80)
    {
        title = title[..80].TrimEnd();
    }

    return title;
}

static IEnumerable<string> Chunk(string text, int chunkSize)
{
    for (var index = 0; index < text.Length; index += chunkSize)
    {
        var length = Math.Min(chunkSize, text.Length - index);
        yield return text.Substring(index, length);
    }
}

static List<SlackTaskPlanItem> UpdateTaskStatus(
    IReadOnlyList<SlackTaskPlanItem> tasks,
    string taskId,
    SlackTaskStatus status)
{
    var updated = new List<SlackTaskPlanItem>(tasks.Count);
    foreach (var task in tasks)
    {
        updated.Add(task.Id == taskId ? task with { Status = status } : task);
    }

    return updated;
}

static void WriteLine(string message)
{
    Console.WriteLine($"[{DateTimeOffset.Now:O}] {message}");
}

internal sealed class HarnessConfiguration
{
    public SlackHarnessSettings Slack { get; set; } = new();

    public static HarnessConfiguration Load()
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Missing configuration file: {path}. Create appsettings.json from the local filler template.");
        }

        var json = File.ReadAllText(path);
        var configuration = JsonSerializer.Deserialize<HarnessConfiguration>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidOperationException("Unable to deserialize appsettings.json.");

        configuration.Slack.Validate();
        return configuration;
    }
}

internal sealed class SlackHarnessSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public string ChannelId { get; set; } = string.Empty;
    public string ThreadTimestamp { get; set; } = string.Empty;
    public string RecipientUserId { get; set; } = string.Empty;
    public string ListenFromTimestamp { get; set; } = string.Empty;
    public int PollingDelaySeconds { get; set; } = 5;
    public int SimulatedProcessingDelayMilliseconds { get; set; } = 1200;
    public int StreamChunkDelayMilliseconds { get; set; } = 60;
    public int StreamChunkSize { get; set; } = 20;
    public string ThinkingStatus { get; set; } = "is thinking";
    public string CompletedStatus { get; set; } = "ready";
    public string SuggestedPromptsTitle { get; set; } = "Try one of these follow-ups";

    public void Validate()
    {
        Require(BotToken, nameof(BotToken));
        Require(UserId, nameof(UserId));
        Require(ChannelId, nameof(ChannelId));
        Require(ThreadTimestamp, nameof(ThreadTimestamp));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"appsettings.json is missing required setting Slack:{name}");
        }
    }
}
