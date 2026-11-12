using Tankstat.Application.Recognition;

namespace Tankstat.Infrastructure.Recognition;

/// <summary>The operator's prompt file (#312): exists, at most the size given, not empty once trimmed; anything else an answer, never a throw.</summary>
internal sealed class PromptFileReader : IPromptFileReader
{
    public PromptFileRead Read(string path, int maxBytes)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return PromptFileRead.Failed(PromptFileProblem.Missing);
            if (file.Length > maxBytes) return PromptFileRead.Failed(PromptFileProblem.TooLarge);
            var text = File.ReadAllText(file.FullName).Trim();
            return text.Length > 0 ? PromptFileRead.Of(text) : PromptFileRead.Failed(PromptFileProblem.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return PromptFileRead.Failed(PromptFileProblem.Unreadable); // a path that is no path at all too
        }
    }
}
