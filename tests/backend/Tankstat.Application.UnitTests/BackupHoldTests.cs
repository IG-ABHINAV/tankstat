using Microsoft.Extensions.Options;
using Tankstat.Application.Backups;
using Tankstat.Application.Images;
using Tankstat.Application.Setup;
using Tankstat.Domain;
using Tankstat.Domain.Instance;

namespace Tankstat.Application.UnitTests;

/// <summary>A held backup: never deleted, whoever asks, until it is released; holding and releasing need an administrator.</summary>
public class BackupHoldTests
{
    private static readonly BackupManifest Manifest = new(
        BackupManifest.CurrentFormat, BackupManifest.Tankstat, "1.0.0", new DateTimeOffset(2026, 9, 30, 2, 0, 0, TimeSpan.Zero), "Sqlite", "AddInstance",
        false, new BackupContents(new Dictionary<string, long>(), true, BackupPhotos.WithPictures, false), new BackupSizes(0, 0, 0, 0, 0), new BackupArchive(BackupArchive.Plain, ""), null);

    /// <summary>A folder of backups by name, each held or not; what was deleted.</summary>
    private sealed class Backups : IBackupStore
    {
        public Dictionary<string, DateTimeOffset?> Points { get; } = [];
        public List<string> Deleted { get; } = [];
        public bool RestorePending { get; init; }
        public RestorePoint Find(string fileName) =>
            Points.TryGetValue(fileName, out var held) ? new RestorePoint(fileName, 100, Manifest, held) : throw new DomainException("backup.notFound", "No such backup.");
        public BackupManifest ReadManifest(string fileName) => Find(fileName).Manifest;
        public void Hold(string fileName, DateTimeOffset? since) => Points[Find(fileName).File] = since;
        public void Delete(string fileName) => Deleted.Add(fileName);
        public IReadOnlyList<RestorePoint> List() => [.. Points.Keys.Select(Find)];
        public string Folder => "backups";
        public IReadOnlyDictionary<string, string?>? ReadSettings(string fileName, BackupManifest manifest, string? identity) => throw new NotSupportedException();
        public string? IdentityFor(BackupManifest manifest, string? passphrase, string? identity) => throw new NotSupportedException();
        public Task RequireRestorableMigrationAsync(BackupManifest manifest, CancellationToken ct) => throw new NotSupportedException();
        public void RequestRestore(RestoreMarker marker) => throw new NotSupportedException();
        public Task<RestorePoint> SaveUploadAsync(Stream content, string? fileName, long maxBytes, CancellationToken ct) => throw new NotSupportedException();
        public Stream OpenRead(string fileName) => throw new NotSupportedException();
    }

    private sealed class Unused : IBackupRunner, IDatabaseFacts, IStorageUsage, IBackupKeyMaker
    {
        public BackupRunStatus Status => throw new NotSupportedException();
        public bool Start(Guid? actorId = null) => throw new NotSupportedException();
        public Task<long?> SizeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> LastMigrationAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> KnownMigrationsAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<FileUsage> MeasureAsync(CancellationToken ct) => throw new NotSupportedException();
        public BackupKeySet Make(string? passphrase) => throw new NotSupportedException();
        public BackupKeySet FromPassphrase(string passphrase) => throw new NotSupportedException();
    }

    private static (World World, Backups Folder, BackupAdminService Service) Admin(bool restorePending = false)
    {
        var w = new World();
        w.Current.SignInAs(w.AddUser("root@x.co", admin: true));
        var folder = new Backups { RestorePending = restorePending };
        folder.Points["held.zip"] = w.Clock.GetUtcNow().AddDays(-1);
        folder.Points["free.zip"] = null;
        var unused = new Unused();
        var code = new OneTimeCode();
        var service = new BackupAdminService(
            w.Access, new SetupGate(new SetupStatus(), new InMemorySetupStore(w.Users, w.Vehicles) { Phase = SetupPhase.Completed }, code), folder, new NoSettingsFile(),
            unused, unused, unused, unused, new BackupOptions().Create(), Options.Create(new StorageOptions()), w.Clock, w.Log.For<BackupAdminService>());
        return (w, folder, service);
    }

    [Fact]
    public async Task AHeldBackup_IsNeverDeleted_TheRefusalSaysToReleaseItFirst()
    {
        var (_, folder, service) = Admin();

        var refused = await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync("held.zip", default));

        Assert.Equal("backup.held", refused.Key);
        Assert.Empty(folder.Deleted);
        await service.DeleteAsync("free.zip", default);
        Assert.Equal(["free.zip"], folder.Deleted);
    }

    [Theory]
    [InlineData("correct horse battery staple", "correct horse battery stapel")]
    [InlineData("correct horse battery staple", null)] // an older client sends no repetition
    [InlineData(null, "correct horse battery staple")] // made up, yet a repetition came: not what the person meant
    public async Task AKeySet_WhosePassphraseWasNotTypedTwiceTheSame_IsRefusedBeforeAnyKeyIsDerived(string? passphrase, string? confirmation)
    {
        var (_, _, service) = Admin(); // its key maker throws when it is reached

        var refused = await Assert.ThrowsAsync<DomainException>(() => service.PrepareEncryptionAsync(passphrase, confirmation, default));

        Assert.Equal("password.mismatch", refused.Key);
    }

    [Fact]
    public async Task AReleasedBackup_CanThenBeDeleted()
    {
        var (_, folder, service) = Admin();

        Assert.False((await service.HoldAsync("held.zip", hold: false, default)).Held);
        await service.DeleteAsync("held.zip", default);

        Assert.Equal(["held.zip"], folder.Deleted);
    }

    [Fact]
    public async Task TheHoldIsRefusedBeforeAWaitingRestore_SoTheReasonIsTheHold()
    {
        var (_, folder, service) = Admin(restorePending: true);

        Assert.Equal("backup.held", (await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync("held.zip", default))).Key);
        Assert.Equal("backup.restorePending", (await Assert.ThrowsAsync<DomainException>(() => service.DeleteAsync("free.zip", default))).Key);
        Assert.Empty(folder.Deleted);
    }

    [Fact]
    public async Task HoldingOneHeldAlready_KeepsTheTimeItWasHeldSince()
    {
        var (w, folder, service) = Admin();
        var since = folder.Points["held.zip"];

        Assert.Equal(since, (await service.HoldAsync("held.zip", hold: true, default)).HeldSince);
        Assert.Equal(w.Clock.GetUtcNow(), (await service.HoldAsync("free.zip", hold: true, default)).HeldSince);
    }

    [Fact]
    public async Task OnlyAnAdministrator_HoldsReleasesOrDeletes()
    {
        var (w, folder, service) = Admin();
        w.Current.SignInAs(w.AddUser("bob@x.co"));

        await Assert.ThrowsAnyAsync<KeyedException>(() => service.HoldAsync("free.zip", hold: true, default));
        await Assert.ThrowsAnyAsync<KeyedException>(() => service.HoldAsync("held.zip", hold: false, default));
        await Assert.ThrowsAnyAsync<KeyedException>(() => service.DeleteAsync("free.zip", default));

        Assert.Null(folder.Points["free.zip"]);
        Assert.NotNull(folder.Points["held.zip"]);
        Assert.Empty(folder.Deleted);
    }
}
