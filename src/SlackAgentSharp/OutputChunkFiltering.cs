namespace SlackAgentSharp;

/// <summary>
/// Decision returned by an <see cref="IOutputChunkFilter"/> for currently buffered output.
/// </summary>
public enum OutputChunkFilterDecision
{
    Undecided,
    Allow,
    Suppress
}

/// <summary>
/// Evaluates buffered output and decides whether it should be streamed to Slack.
/// </summary>
public interface IOutputChunkFilter
{
    /// <summary>
    /// Evaluates the current buffered output.
    /// </summary>
    /// <param name="bufferedOutput">Output accumulated so far for the current block.</param>
    /// <returns>A filter decision for the current buffer.</returns>
    OutputChunkFilterDecision EvaluateBufferedOutput(ReadOnlySpan<char> bufferedOutput);
}

/// <summary>
/// Filter that always allows output.
/// </summary>
public sealed class AllowAllOutputChunkFilter : IOutputChunkFilter
{
    public static readonly AllowAllOutputChunkFilter Instance = new();

    private AllowAllOutputChunkFilter()
    {
    }

    public OutputChunkFilterDecision EvaluateBufferedOutput(ReadOnlySpan<char> bufferedOutput)
    {
        return OutputChunkFilterDecision.Allow;
    }
}

/// <summary>
/// Filter that suppresses output starting with any configured prefix.
/// </summary>
public sealed class PrefixOutputChunkFilter : IOutputChunkFilter
{
    private readonly string[] _suppressedPrefixes;

    public PrefixOutputChunkFilter(IEnumerable<string> suppressedPrefixes)
    {
        if (suppressedPrefixes is null)
        {
            throw new ArgumentNullException(nameof(suppressedPrefixes));
        }

        _suppressedPrefixes = suppressedPrefixes
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (_suppressedPrefixes.Length == 0)
        {
            throw new ArgumentException("At least one non-empty prefix is required.", nameof(suppressedPrefixes));
        }
    }

    public OutputChunkFilterDecision EvaluateBufferedOutput(ReadOnlySpan<char> bufferedOutput)
    {
        var trimmed = bufferedOutput.TrimStart();
        if (trimmed.IsEmpty)
        {
            return OutputChunkFilterDecision.Undecided;
        }

        var maybeMatch = false;
        foreach (var prefix in _suppressedPrefixes)
        {
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
            {
                return OutputChunkFilterDecision.Suppress;
            }

            if (prefix.AsSpan().StartsWith(trimmed, StringComparison.Ordinal))
            {
                maybeMatch = true;
            }
        }

        return maybeMatch ? OutputChunkFilterDecision.Undecided : OutputChunkFilterDecision.Allow;
    }
}
