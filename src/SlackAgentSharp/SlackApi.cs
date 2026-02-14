using System.Text.Json.Serialization;

namespace SlackAgentSharp;

internal sealed record SlackMessageRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("text")] string Text);

internal sealed record SlackThreadMessageRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("thread_ts")] string ThreadTimestamp);

internal sealed record SlackBlockMessageRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("blocks")] IReadOnlyList<object> Blocks,
    [property: JsonPropertyName("thread_ts")] string? ThreadTimestamp);

internal sealed record SlackBlockMessageUpdateRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("ts")] string Timestamp,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("blocks")] IReadOnlyList<object> Blocks);

internal sealed record SlackAssistantThreadStatusRequest(
    [property: JsonPropertyName("channel_id")] string ChannelId,
    [property: JsonPropertyName("thread_ts")] string ThreadTimestamp,
    [property: JsonPropertyName("status")] string Status);

internal sealed record SlackAssistantThreadTitleRequest(
    [property: JsonPropertyName("channel_id")] string ChannelId,
    [property: JsonPropertyName("thread_ts")] string ThreadTimestamp,
    [property: JsonPropertyName("title")] string Title);

internal sealed record SlackAssistantThreadSuggestedPromptsRequest(
    [property: JsonPropertyName("channel_id")] string ChannelId,
    [property: JsonPropertyName("thread_ts")] string ThreadTimestamp,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("prompts")] IReadOnlyList<SlackSuggestedPrompt> Prompts);

internal sealed record SlackStreamStartRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("thread_ts")] string ThreadTimestamp,
    [property: JsonPropertyName("markdown_text")] string? MarkdownText,
    [property: JsonPropertyName("recipient_team_id")] string? RecipientTeamId,
    [property: JsonPropertyName("recipient_user_id")] string? RecipientUserId);

internal sealed record SlackStreamAppendRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("ts")] string StreamTimestamp,
    [property: JsonPropertyName("markdown_text")] string MarkdownText);

internal sealed record SlackStreamStopRequest(
    [property: JsonPropertyName("channel")] string Channel,
    [property: JsonPropertyName("ts")] string StreamTimestamp,
    [property: JsonPropertyName("markdown_text")] string? MarkdownText);

internal sealed record SlackConversationOpenRequest(
    [property: JsonPropertyName("users")] string Users);

internal sealed record SlackConversationOpenResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("channel")] SlackChannel? Channel,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record SlackChannel(
    [property: JsonPropertyName("id")] string? Id);

internal sealed record SlackApiResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("ts")] string? Timestamp,
    [property: JsonPropertyName("error")] string? Error);

public sealed record SlackMessage(
    [property: JsonPropertyName("ts")] string? Timestamp,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("user")] string? User,
    [property: JsonPropertyName("bot_id")] string? BotId,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("thread_ts")] string? ThreadTimestamp);

public sealed record SlackSuggestedPrompt(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("message")] string Message);

internal sealed record SlackConversationHistoryResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("messages")] List<SlackMessage>? Messages,
    [property: JsonPropertyName("error")] string? Error);

internal sealed record PlanBlock(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("tasks")] IReadOnlyList<PlanTaskBlock> Tasks)
{
    public PlanBlock(string title, IReadOnlyList<PlanTaskBlock> tasks)
        : this("plan", title, tasks)
    {
    }
}

internal sealed record PlanTaskBlock(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("status")] string Status);


