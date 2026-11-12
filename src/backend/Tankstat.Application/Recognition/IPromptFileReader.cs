namespace Tankstat.Application.Recognition;

/// <summary>Why the operator's prompt file gave no prompt (#312: read by Infrastructure, worded by <see cref="RecognitionSetup"/>).</summary>
public enum PromptFileProblem
{
    Missing,
    TooLarge,
    Empty,
    Unreadable,
}

/// <summary>The prompt file's text (trimmed) when it gave one, else why not.</summary>
public sealed record PromptFileRead(string? Text, PromptFileProblem? Problem)
{
    public static PromptFileRead Of(string text) => new(text, null);
    public static PromptFileRead Failed(PromptFileProblem problem) => new(null, problem);
}

/// <summary>
/// Reads <c>Recognition:OpenAiCompatible:SystemPromptFile</c> (#312: Application reads no file itself): never throws, a problem is an answer,
/// which <see cref="RecognitionSetup"/> turns into "off with a warning" naming the setting only.
/// </summary>
public interface IPromptFileReader
{
    /// <param name="maxBytes">A file larger than this is the wrong file, not a prompt.</param>
    PromptFileRead Read(string path, int maxBytes);
}
