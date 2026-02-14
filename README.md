# SlackAgentSharp

Lightweight .NET client for Slack messaging workflows. This library is designed for agent-style applications: it handles Slack Web API calls, thread/message streaming helpers, and plan/status payload shaping. LLM orchestration, tool routing, and app-specific behavior should live in the calling application.

> [!NOTE]
> Portions of this project were developed with LLMs such as Codex 5.2, GLM 4.7, and Qwen3.

Slack Web API docs: `https://api.slack.com/methods`

Slack AI docs: `https://docs.slack.dev/ai/`

## Agent-Focused Features
This library focuses on the Slack AI interaction patterns showcased by Slack:
- Loading states via `SetAssistantThreadStatusAsync`.
- Thread titles via `SetAssistantThreadTitleAsync` (useful for LLM-generated conversation summaries).
- Suggested prompts via `SetAssistantThreadSuggestedPromptsAsync`.
- Threaded conversations via `SendThreadMessageAsync` and conversation reply/history APIs.
- Text streaming via `StartMessageStreamAsync`, `AppendMessageStreamAsync`, and `StopMessageStreamAsync` (or `SlackOutputStreamManager`).
- Plan blocks via `SlackPlan`, `SlackTaskPlan`, and related plan models.

## Quick Start
```csharp
using SlackAgentSharp;

var options = new SlackOptions
{
    BotToken = Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN") ?? "",
    // Example: suppress internal tool-call payloads from being streamed into Slack messages.
    OutputChunkFilter = new PrefixOutputChunkFilter(new[]
    {
        "<tool_call",
        "{\"type\":\"tool_call\""
    })
};

using var client = new SlackClient(options);

await client.SendMessageAsync("C0123456789", "Hello from SlackAgentSharp!");

var threadTs = await client.SendMessageWithTimestampAsync("C0123456789", "Parent message");
if (!string.IsNullOrWhiteSpace(threadTs))
{
    await client.SendThreadMessageAsync("C0123456789", threadTs, "Reply in thread.");
}
```

## Streaming Helpers
- Use `StartMessageStreamAsync`, `AppendMessageStreamAsync`, and `StopMessageStreamAsync` for low-level streamed output calls.
- Use `SlackOutputStreamManager` when output arrives in chunks and you want buffered/suppressed handling in one place.
- Output suppression is pluggable via `IOutputChunkFilter` (`AllowAllOutputChunkFilter` and `PrefixOutputChunkFilter` included).

Example with `SlackOutputStreamManager`:
```csharp
var manager = new SlackOutputStreamManager(
    client,
    options,
    "C0123456789",
    threadTs!,
    recipientUserId: "");

await manager.OnOutputBlockStarted(ct);
await manager.OnOutputChunk("Hello ", ct);
await manager.OnOutputChunk("world", ct);
await manager.OnOutputBlockEnded(ct);
```

## Direct Messages and Conversation Reads
- Use `SendDirectMessageAsync(userId, message)` to open a DM channel and send in one call.
- Use `GetConversationMessagesAsync` and `GetConversationRepliesAsync` to read channel/thread history.

## Assistant Thread Metadata
- Use `SetAssistantThreadStatusAsync` to show loading/progress states.
- Use `SetAssistantThreadTitleAsync` to set a concise, LLM-generated thread title in conversation history.
- Use `SetAssistantThreadSuggestedPromptsAsync` to add clickable follow-up prompt suggestions.

## Plan Message Support
- `SlackPlan` helps publish and update structured plan blocks.
- `SlackTaskPlan`, `SlackTaskPlanItem`, and `SlackTaskStatus` provide a typed model for status updates.

## Security
### Authentication
Set `SlackOptions.BotToken` to a valid bot token (for example, `xoxb-...`). Keep tokens in a secure secret store and avoid committing them to source control.

### Transport and Resilience
- Requests use retry-aware per-attempt timeouts from `SlackOptions` (`RequestTimeoutSeconds`, `TransientRetryCount`, `RetryDelayMilliseconds`).
- Response bodies are size-capped via `MaxResponseBodyBytes`.

## Notes
- This package intentionally does not prescribe your LLM provider or tool-calling protocol.
- Prefer filtering tool-call/control output before it reaches this library when your provider offers structured event streams.

## Releases
Publishing a GitHub Release triggers the `Release Build` workflow in `.github/workflows/release.yml`.
That workflow builds the library in `Release` mode and attaches a zip bundle (DLL + symbols/docs when present) to the release.

## Contributing
Pull requests and issues are open and accepted on this project.
