namespace Tankstat.Application.Backups;

/// <summary>The database as the administration page shows it next to the backups: how large it is, and the migration it is at.</summary>
public interface IDatabaseFacts
{
    /// <summary>Bytes the database takes on its server or disk; null where the provider will not say.</summary>
    Task<long?> SizeAsync(CancellationToken ct);

    /// <summary>The last applied migration (its id): a backup made at it is restored as it is, one made at an older migration brought up to date.</summary>
    Task<string?> LastMigrationAsync(CancellationToken ct);

    /// <summary>This release's migrations (their ids on the provider), oldest first: a backup made at one of them can be restored here.</summary>
    Task<IReadOnlyList<string>> KnownMigrationsAsync(CancellationToken ct);
}

/// <summary>Bytes of the uploaded files: the pictures root, and the photos root when it is a folder of its own (null: the photos are with the pictures).</summary>
public sealed record FileUsage(long Pictures, long? Photos);

public interface IStorageUsage
{
    Task<FileUsage> MeasureAsync(CancellationToken ct);
}

/// <summary>
/// A key pair for encrypted backups, made from a passphrase: the passphrase itself (made up when none was given), the identity that opens
/// the backups, and the two settings that make the server encrypt for it (the recipient and the salt). Shown once, never stored or logged.
/// </summary>
public sealed record BackupKeySet(string Passphrase, bool Generated, string Identity, string Recipient, string Salt);

public interface IBackupKeyMaker
{
    /// <summary>The key set of a passphrase with a new salt; with none, a passphrase is made up. <c>backup.passphraseTooShort</c> below the minimum.</summary>
    BackupKeySet Make(string? passphrase);

    /// <summary>
    /// The key set of <paramref name="passphrase"/> as given, with a new salt (#313: <c>backup keygen</c>, <c>backup --passphrase-stdin</c>). No
    /// minimum is checked and no Backups setting read: the command line checks the passphrase itself, and works under Backups settings the
    /// start would refuse, the half key pair it is run to replace among them.
    /// </summary>
    BackupKeySet FromPassphrase(string passphrase);
}
