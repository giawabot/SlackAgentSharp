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
var lastSeenTimestamp = CurrentSlackTimestamp();

var channelId = await slackClient.OpenDirectMessageChannelAsync(settings.UserId, cancellationSource.Token)
    ?? throw new InvalidOperationException("Unable to open DM channel for configured Slack:UserId.");

WriteLine("E2E harness started.");
WriteLine($"Watching DM channel {channelId} for user {settings.UserId}.");

while (!cancellationSource.IsCancellationRequested)
{
    try
    {
        var messages = await slackClient.GetConversationMessagesAsync(
            channelId,
            lastSeenTimestamp,
            cancellationSource.Token);

        foreach (var message in messages.OrderBy(message => ParseTimestamp(message.Timestamp ?? "0")))
        {
            var reason = GetSkipReason(message, settings.UserId, lastSeenTimestamp);
            var preview = string.IsNullOrWhiteSpace(message.Text)
                ? "(empty)"
                : message.Text!.Replace('\n', ' ').Trim();
            if (preview.Length > 80)
            {
                preview = preview[..80];
            }

            WriteLine(
                $"Seen ts={message.Timestamp ?? "(null)"} user={message.User ?? "(null)"} " +
                $"bot={message.BotId ?? "(null)"} subtype={message.Subtype ?? "(null)"} " +
                $"thread={message.ThreadTimestamp ?? "(null)"} reason={(reason ?? "process")} text=\"{preview}\"");
        }

        var threadedReplies = new List<SlackMessage>();
        foreach (var threadRoot in messages.Where(IsAssistantThreadBootstrap))
        {
            var threadTs = string.IsNullOrWhiteSpace(threadRoot.ThreadTimestamp)
                ? threadRoot.Timestamp
                : threadRoot.ThreadTimestamp;
            if (string.IsNullOrWhiteSpace(threadTs))
            {
                continue;
            }

            var replies = await slackClient.GetConversationRepliesAsync(
                channelId,
                threadTs,
                lastSeenTimestamp,
                cancellationSource.Token);

            foreach (var reply in replies.OrderBy(reply => ParseTimestamp(reply.Timestamp ?? "0")))
            {
                var reason = GetSkipReason(reply, settings.UserId, lastSeenTimestamp);
                var preview = string.IsNullOrWhiteSpace(reply.Text)
                    ? "(empty)"
                    : reply.Text!.Replace('\n', ' ').Trim();
                if (preview.Length > 80)
                {
                    preview = preview[..80];
                }

                WriteLine(
                    $"ThreadReply ts={reply.Timestamp ?? "(null)"} user={reply.User ?? "(null)"} " +
                    $"bot={reply.BotId ?? "(null)"} subtype={reply.Subtype ?? "(null)"} " +
                    $"thread={reply.ThreadTimestamp ?? "(null)"} reason={(reason ?? "process")} text=\"{preview}\"");
            }

            threadedReplies.AddRange(replies);
        }

        var pendingMessages = messages
            .Concat(threadedReplies)
            .Where(message => IsUserMessage(message, settings.UserId))
            .Where(message => IsStrictlyAfter(message.Timestamp, lastSeenTimestamp))
            .OrderBy(message => ParseTimestamp(message.Timestamp!))
            .DistinctBy(message => message.Timestamp)
            .ToList();

        WriteLine(
            $"Polled {messages.Count} DM messages since {lastSeenTimestamp}; " +
            $"{pendingMessages.Count} matched processing criteria.");

        foreach (var message in pendingMessages)
        {
            if (message.Timestamp is null)
            {
                continue;
            }

            lastSeenTimestamp = message.Timestamp;
            await ProcessMessageAsync(
                slackClient,
                settings,
                channelId,
                settings.UserId,
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
    SlackHarnessSettings settings,
    string channelId,
    string recipientUserId,
    SlackMessage message,
    TimeSpan processingDelay,
    TimeSpan chunkDelay,
    int chunkSize,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(message.Timestamp))
    {
        return;
    }

    var threadTimestamp = string.IsNullOrWhiteSpace(message.ThreadTimestamp)
        ? message.Timestamp
        : message.ThreadTimestamp;
    if (string.IsNullOrWhiteSpace(threadTimestamp))
    {
        return;
    }

    var planPublisher = new SlackPlan(slackClient, channelId, threadTimestamp);
    var streamManager = new SlackOutputStreamManager(
        slackClient,
        AllowAllOutputChunkFilter.Instance,
        channelId,
        threadTimestamp,
        recipientUserId);

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
            channelId,
            threadTimestamp,
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
            channelId,
            threadTimestamp,
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
            channelId,
            threadTimestamp,
            settings.SuggestedPromptsTitle,
            prompts,
            cancellationToken),
        "assistant.threads.setSuggestedPrompts");

    await EnsureTrue(
        slackClient.SetAssistantThreadStatusAsync(
            channelId,
            threadTimestamp,
            string.Empty,
            cancellationToken),
        "assistant.threads.setStatus(clear)");

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

    if (IsAssistantThreadBootstrap(message))
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(message.User))
    {
        return false;
    }

    if (string.IsNullOrWhiteSpace(expectedUserId))
    {
        return true;
    }

    return string.Equals(message.User, expectedUserId, StringComparison.Ordinal);
}

static bool IsAssistantThreadBootstrap(SlackMessage message)
{
    return string.Equals(message.Subtype, "assistant_app_thread", StringComparison.Ordinal);
}

static bool IsStrictlyAfter(string? timestamp, string boundary)
{
    if (string.IsNullOrWhiteSpace(timestamp))
    {
        return false;
    }

    return ParseTimestamp(timestamp) > ParseTimestamp(boundary);
}

static string? GetSkipReason(SlackMessage message, string expectedUserId, string boundaryTimestamp)
{
    if (string.IsNullOrWhiteSpace(message.Timestamp))
    {
        return "missing_timestamp";
    }

    if (!IsStrictlyAfter(message.Timestamp, boundaryTimestamp))
    {
        return "not_after_boundary";
    }

    if (!string.IsNullOrWhiteSpace(message.BotId))
    {
        return "bot_message";
    }

    if (string.IsNullOrWhiteSpace(message.User))
    {
        return "missing_user";
    }

    if (!string.IsNullOrWhiteSpace(expectedUserId)
        && !string.Equals(message.User, expectedUserId, StringComparison.Ordinal))
    {
        return $"user_mismatch(expected:{expectedUserId})";
    }

    return null;
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

static string CurrentSlackTimestamp()
{
    var now = DateTimeOffset.UtcNow;
    return $"{now.ToUnixTimeSeconds()}.{now.Millisecond * 1000:D6}";
}

internal sealed class HarnessConfiguration
{
    public SlackHarnessSettings Slack { get; set; } = new();

    public static HarnessConfiguration Load()
    {
        var path = ResolveConfigPath();

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

    private static string ResolveConfigPath()
    {
        var tried = new List<string>();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in roots)
        {
            var current = root;
            while (!string.IsNullOrWhiteSpace(current))
            {
                var direct = Path.Combine(current, "appsettings.json");
                tried.Add(direct);
                if (File.Exists(direct))
                {
                    return direct;
                }

                var nested = Path.Combine(current, "tests", "SlackAgentSharp.E2E", "appsettings.json");
                tried.Add(nested);
                if (File.Exists(nested))
                {
                    return nested;
                }

                current = Directory.GetParent(current)?.FullName;
            }
        }

        throw new FileNotFoundException(
            "Missing configuration file appsettings.json. Tried: " + string.Join("; ", tried.Distinct(StringComparer.OrdinalIgnoreCase)));
    }
}

internal sealed class SlackHarnessSettings
{
    public string BotToken { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public int PollingDelaySeconds { get; set; } = 5;
    public int SimulatedProcessingDelayMilliseconds { get; set; } = 1200;
    public int StreamChunkDelayMilliseconds { get; set; } = 60;
    public int StreamChunkSize { get; set; } = 20;
    public string ThinkingStatus { get; set; } = "is thinking";
    public string SuggestedPromptsTitle { get; set; } = "Try one of these follow-ups";

    public void Validate()
    {
        Require(BotToken, nameof(BotToken));
        Require(UserId, nameof(UserId));
    }

    private static void Require(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"appsettings.json is missing required setting Slack:{name}");
        }
    }
}
