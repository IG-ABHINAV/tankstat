using Tankstat.Application.Backups;
using Tankstat.Infrastructure.Settings;

namespace Tankstat.Infrastructure.Backups;

/// <summary><see cref="IBackupArchive"/> over <see cref="BackupFile"/> (#313): a backup anywhere on disk, as the <c>decrypt</c> command reads it.</summary>
internal sealed class BackupFileArchive : IBackupArchive
{
    public BackupManifest ReadManifest(string path) => BackupFile.ReadManifest(path);

    public void VerifyChecksum(string path, BackupManifest manifest) => BackupFile.VerifyChecksum(path, manifest);

    public byte[] IdentityOf(BackupManifest manifest, string passphrase) => BackupFile.IdentityOf(manifest, passphrase);

    public void Unpack(string path, BackupManifest manifest, byte[]? identity, string folder)
    {
        BackupFile.Extract(path, manifest, identity, folder);
        // The settings file beside the tables, owner-only like the rest: it may hold secrets (Backups:Secrets).
        if (BackupFile.ReadSettings(path, manifest, identity) is not { } settings) return;
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = SettingsFileStore.OwnerOnly;
        using var stream = new FileStream(Path.Combine(folder, BackupSettingsFile.Plain), options);
        using var writer = new StreamWriter(stream);
        writer.Write(SettingsFileStore.ToJson(settings));
    }
}

/// <summary><see cref="IStartupRestore"/> over <see cref="StartupSteps"/>: the start's own steps, from the services they are registered in.</summary>
internal sealed class StartupRestore(IServiceProvider services) : IStartupRestore
{
    public Task<RestoreOutcome> RestoreAndMigrateAsync(CancellationToken ct) => StartupSteps.RestoreAndMigrateAsync(services, ct);
}
