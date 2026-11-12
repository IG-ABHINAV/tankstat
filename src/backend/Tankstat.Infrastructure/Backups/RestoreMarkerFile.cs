using System.Text.Json;
using Tankstat.Application.Backups;

namespace Tankstat.Infrastructure.Backups;

/// <summary>
/// The restore marker's file (#312: moved here from the record, Application touching no file): written readable by the app's user only, since
/// it carries a backup's identity in plain, and read back.
/// </summary>
public static class RestoreMarkerFile
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>Writes the marker into a backups folder, readable by the app's user only; a marker already there is replaced.</summary>
    public static void Write(this RestoreMarker marker, string backupsRoot)
    {
        Directory.CreateDirectory(backupsRoot);
        marker.WriteTo(RestoreMarker.PathIn(backupsRoot));
    }

    /// <summary>Writes the marker at a path, readable by the app's user only before anything is in it; a file already there is replaced.</summary>
    public static void WriteTo(this RestoreMarker marker, string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        using var stream = new FileStream(path, options);
        // One left by an older run keeps its mode otherwise, and the identity would be written into a file others may read.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stream.SafeFileHandle, OwnerOnly);
        JsonSerializer.Serialize(stream, marker, BackupManifest.Json);
    }

    /// <summary>The marker at a path; a <see cref="JsonException"/> when the file is not one.</summary>
    public static RestoreMarker Read(string path) =>
        JsonSerializer.Deserialize<RestoreMarker>(File.ReadAllText(path), BackupManifest.Json) ?? throw new JsonException("The marker is empty.");
}
