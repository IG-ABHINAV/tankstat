namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The file system's answer the Server settings page's certificate check asks (#312).</summary>
public sealed class FileProbeTests
{
    [Fact]
    public void AFileThatIsThere_Exists_AndOneThatIsNot_DoesNot()
    {
        var file = Path.GetTempFileName();
        try
        {
            var probe = new FileProbe();

            Assert.True(probe.Exists(file));
            Assert.False(probe.Exists(file + ".missing"));
            Assert.False(probe.Exists(Path.GetTempPath())); // a folder is no file
        }
        finally
        {
            File.Delete(file);
        }
    }
}
