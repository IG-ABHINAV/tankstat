namespace Tankstat.Application.Backups;

/// <summary>
/// What the backup commands of <c>dotnet Tankstat.Api.dll</c> do (#313), behind the Application's ports as the Backups page's service is
/// (<see cref="BackupAdminService"/>): the command line keeps its arguments, prompts and the lines it prints. A singleton, every port it uses
/// one too: a command runs from the root services, with no request scope. A passphrase it is given was checked by the command already
/// (typed twice, at least the minimum), so nothing here reads the Backups settings beyond what writing a backup reads.
/// </summary>
public sealed class BackupCommandService(IBackupKeyMaker keys, IBackupWriter writer, IBackupArchive archive, IStartupRestore startup)
{
    /// <summary><c>backup keygen</c>: the recipient, the salt and the identity of <paramref name="passphrase"/>.</summary>
    public BackupKeySet Keygen(string passphrase) => keys.FromPassphrase(passphrase);

    /// <summary><c>backup</c>: writes one, encrypted for a new passphrase when one is given, else as the settings say.</summary>
    public Task<BackupResult> WriteAsync(BackupRequest request, string? newPassphrase, CancellationToken ct)
    {
        if (newPassphrase is not null)
        {
            var key = keys.FromPassphrase(newPassphrase);
            request = request with { Encryption = new BackupEncryption(BackupEncryption.AgeX25519, key.Recipient, key.Salt, Argon2idParameters.Default) };
        }
        return writer.WriteAsync(request, ct);
    }

    /// <summary>
    /// <c>decrypt</c>: the manifest and the checksums first, then the identity (<paramref name="identityOf"/>, asked only then, so a file that is
    /// no backup says so before a passphrase is typed; null for a plain backup), then the tables, files, keys and settings file into
    /// <paramref name="folder"/>. The manifest, for what the command says it unpacked.
    /// </summary>
    public BackupManifest Decrypt(string file, string folder, Func<BackupManifest, byte[]?> identityOf)
    {
        var manifest = archive.ReadManifest(file);
        archive.VerifyChecksum(file, manifest);
        archive.Unpack(file, manifest, identityOf(manifest), folder);
        return manifest;
    }

    /// <summary>The identity of a passphrase for this backup, derived as its manifest says.</summary>
    public byte[] IdentityOf(BackupManifest manifest, string passphrase) => archive.IdentityOf(manifest, passphrase);

    /// <summary><c>restore --now</c>: the waiting restore applied at once, then the migrations, as the server's next start would.</summary>
    public Task<RestoreOutcome> RestoreNowAsync(CancellationToken ct) => startup.RestoreAndMigrateAsync(ct);
}
