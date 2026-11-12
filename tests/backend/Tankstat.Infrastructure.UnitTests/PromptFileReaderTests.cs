using Tankstat.Application.Recognition;
using Tankstat.Infrastructure.Recognition;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The operator's prompt file read from disk (#312, moved from RecognitionSetup): its text trimmed, or why not, never a throw.</summary>
public sealed class PromptFileReaderTests
{
    private const int Max = RecognitionSetup.MaxPromptFileBytes;
    private readonly PromptFileReader _reader = new();

    [Fact]
    public void AFileWithAPrompt_GivesItsTextTrimmed()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "  From the file.\n");
            Assert.Equal(PromptFileRead.Of("From the file."), _reader.Read(file, Max));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void AFileThatIsMissing_Empty_FarTooLarge_OrNoPathAtAll_SaysWhich()
    {
        var empty = Path.GetTempFileName();
        var huge = Path.GetTempFileName();
        try
        {
            File.WriteAllText(empty, "  \n");
            File.WriteAllText(huge, new string('x', Max + 1));

            Assert.Equal(PromptFileProblem.Missing, _reader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), Max).Problem);
            Assert.Equal(PromptFileProblem.Empty, _reader.Read(empty, Max).Problem);
            Assert.Equal(PromptFileProblem.TooLarge, _reader.Read(huge, Max).Problem);
            Assert.Equal(PromptFileProblem.Unreadable, _reader.Read("prompt\0.txt", Max).Problem); // no path at all: an answer, not a throw
            Assert.Null(_reader.Read(huge, Max).Text);
        }
        finally
        {
            File.Delete(empty);
            File.Delete(huge);
        }
    }
}
