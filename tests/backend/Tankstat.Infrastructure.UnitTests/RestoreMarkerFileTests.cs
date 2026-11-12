using System.Text.Json;
using Tankstat.Application.Backups;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Settings;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The restore marker's file (#312): owner-only, replacing one another run left, and a file that is not a marker refused.</summary>
public sealed class RestoreMarkerFileTests : IDisposable
{
    private const UnixFileMode OwnerOnly = SettingsFileStore.OwnerOnly;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tankstat-marker-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void AMarker_IsWrittenIntoAFolderItMakes_ReadableByTheAppsUserOnly_AndReadBack()
    {
        var marker = new RestoreMarker("tankstat-backup-1.zip", Guid.NewGuid(), new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero), "AGE-SECRET-KEY-1ZZZZ");

        marker.Write(_folder);

        var path = RestoreMarker.PathIn(_folder);
        if (!OperatingSystem.IsWindows()) Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal(marker, RestoreMarkerFile.Read(path));
    }

    [Fact]
    public void AFileAnOlderRunLeftReadableByOthers_IsReplaced_AndEndsOwnerOnly()
    {
        Directory.CreateDirectory(_folder);
        var path = RestoreMarker.PathIn(_folder);
        File.WriteAllText(path, "left by an older run");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        new RestoreMarker("tankstat-backup-2.zip", Guid.Empty, DateTimeOffset.UtcNow, "AGE-SECRET-KEY-1YYYY").WriteTo(path);

        if (!OperatingSystem.IsWindows()) Assert.Equal(OwnerOnly, File.GetUnixFileMode(path));
        Assert.Equal("tankstat-backup-2.zip", RestoreMarkerFile.Read(path).File);
    }

    [Theory]
    [InlineData("this is { not json")]
    [InlineData("null")]
    public void AFileThatIsNotAMarker_IsAJsonException(string text)
    {
        Directory.CreateDirectory(_folder);
        var path = RestoreMarker.PathIn(_folder);
        File.WriteAllText(path, text);

        Assert.ThrowsAny<JsonException>(() => RestoreMarkerFile.Read(path));
    }
}
