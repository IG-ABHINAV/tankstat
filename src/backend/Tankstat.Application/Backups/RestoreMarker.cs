namespace Tankstat.Application.Backups;

/// <summary>
/// A restore that waits for the next start: <c>restore.pending</c> in the backups folder names the backup (a file name in that folder, never
/// a path), who asked and when, and carries the identity that opens an encrypted backup (in plain, which is why the file is readable by the
/// app's user only and goes with the restart). The start-up step applies it and removes it, or refuses it and keeps it renamed, without the
/// identity. The record and its JSON shape (<see cref="BackupManifest.Json"/>) are here; writing and reading the file are Infrastructure's
/// (<c>RestoreMarkerFile</c>, #312).
/// </summary>
public sealed record RestoreMarker(string File, Guid RequestedById, DateTimeOffset RequestedAt, string? Identity)
{
    public const string FileName = "restore.pending";

    /// <summary>Where the marker lies in a backups folder (a path only: nothing is read or written here).</summary>
    public static string PathIn(string backupsRoot) => System.IO.Path.Combine(backupsRoot, FileName);
}
