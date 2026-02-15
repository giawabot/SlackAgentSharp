# SlackAgentSharp E2E Harness

This project is an internal end-to-end verifier for `SlackAgentSharp` behavior in real Slack DMs.

> [!NOTE]
> Portions of this project were developed with LLMs such as Codex 5.2, GLM 4.7, and Qwen3.

## What It Tests

For each new user message in a DM conversation:

- Polls Slack for new messages after harness startup time.
- Detects assistant-thread bootstrap events and reads thread replies.
- Simulates processing delay.
- Sends assistant loading status.
- Publishes and updates a plan block.
- Streams chunked response output.
- Sets thread title.
- Sets suggested prompts.
- Clears loading status when complete.

## Prerequisites

- .NET 8 SDK
- A Slack app with a bot token and AI/thread permissions suitable for:
  - DM channel access/history
  - sending/updating messages
  - assistant thread APIs
  - streaming APIs

## Local Configuration

Create a local file (not committed):

`tests/SlackAgentSharp.E2E/appsettings.json`

Example:

```json
{
  "Slack": {
    "BotToken": "xoxb-...",
    "UserId": "U1234567890",
    "PollingDelaySeconds": 5,
    "SimulatedProcessingDelayMilliseconds": 1200,
    "StreamChunkDelayMilliseconds": 60,
    "StreamChunkSize": 20,
    "ThinkingStatus": "is thinking",
    "SuggestedPromptsTitle": "Try one of these follow-ups"
  }
}
```

Notes:

- `UserId` is the member ID whose DM messages should trigger processing.
- If `appsettings.json` is missing, the harness throws at startup.
- `tests/SlackAgentSharp.E2E/appsettings.json` is git-ignored.

## Run

From repo root:

```bash
dotnet run --project tests/SlackAgentSharp.E2E/SlackAgentSharp.E2E.csproj
```

Stop with `Ctrl+C`.

## Diagnostics

The harness logs each poll cycle and message classification. Useful fields:

- `reason=process`: message will be handled.
- `reason=user_mismatch(...)`: incoming user ID does not match configured `UserId`.
- `reason=not_after_boundary`: message timestamp is not newer than harness boundary.
- `subtype=assistant_app_thread`: assistant bootstrap event; harness will also query thread replies.

## Safety

- Do not commit bot tokens.
- Use this harness only for internal verification.
