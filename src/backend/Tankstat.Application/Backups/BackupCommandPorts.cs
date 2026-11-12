namespace Tankstat.Application.Backups;

/// <summary>What the start did with a restore that waited for it (the restore step's outcome; <see cref="IStartupRestore"/>).</summary>
public enum RestoreOutcome
{
    /// <summary>No restore waited.</summary>
    None,
    /// <summary>The data is the backup's.</summary>
    Applied,
    /// <summary>Refused before anything was replaced: the data is as it was (<c>restore.refused.&lt;time&gt;</c>).</summary>
    Refused,
    /// <summary>
    /// The data on the server was replaced (or its tables removed), then the restore failed: what was there before is gone, the files may be
    /// half copied (<c>restore.failed.&lt;time&gt;</c>).
    /// </summary>
    Failed,
}

/// <summary>
/// A backup file anywhere on disk, read as the <c>decrypt</c> command reads one (#313), never only the backups folder's (<see cref="IBackupStore"/>).
/// Each step throws what the file gives (<c>backup.corrupt</c>, <c>backup.wrongKey</c>, an <see cref="IOException"/>), unwrapped.
/// </summary>
public interface IBackupArchive
{
    /// <summary>The manifest, found by name; a file that is no whole Tankstat backup is <c>backup.corrupt</c>.</summary>
    BackupManifest ReadManifest(string path);

    /// <summary>The checksum of the inner archive and of the settings entry against the manifest.</summary>
    void VerifyChecksum(string path, BackupManifest manifest);

    /// <summary>The identity of a passphrase for this backup, derived as the manifest says (<c>backup.notEncrypted</c> for a plain one).</summary>
    byte[] IdentityOf(BackupManifest manifest, string passphrase);

    /// <summary>
    /// Unpacks the tables, files and keys into an empty folder (made owner-only), and the settings file beside them as <c>settings.json</c>,
    /// owner-only too: it may hold secrets. An encrypted backup's identity is checked against its recipient before anything is read.
    /// </summary>
    void Unpack(string path, BackupManifest manifest, byte[]? identity, string folder);
}

/// <summary>
/// The start's two steps for a restore that waits, run at once while the server is stopped (<c>restore --now</c>): the restore, then the
/// migrations, as a start would run them.
/// </summary>
public interface IStartupRestore
{
    Task<RestoreOutcome> RestoreAndMigrateAsync(CancellationToken ct);
}
