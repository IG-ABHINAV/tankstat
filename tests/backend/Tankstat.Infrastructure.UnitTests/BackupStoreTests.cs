using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Tankstat.Application;
using Tankstat.Application.Backups;
using Tankstat.Application.Images;
using Tankstat.Domain;
using Tankstat.Domain.Backups;
using Tankstat.Infrastructure.Backups;
using Tankstat.Infrastructure.Persistence;
using Tankstat.Infrastructure.Storage;

namespace Tankstat.Infrastructure.UnitTests;

/// <summary>The backups folder as the administration page uses it: the restore points in it, uploads into it, downloads out of it.</summary>
public sealed class BackupStoreTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"tankstat-store-{Guid.NewGuid():N}");
    private string Backups => Path.Combine(_data, "backups");

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    private FileBackupStore Store() => new(new FixedMonitor<BackupOptions>(new BackupOptions { Path = Backups }), new NoDatabase());

    /// <summary>The folder's tests never ask the database (only the migration check of a restore does): a factory that says so if it is used.</summary>
    private sealed class NoDatabase : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => throw new InvalidOperationException("The backups folder's tests use no database.");
    }

    /// <summary>A real backup file (of a seeded SQLite instance), written outside the backups folder.</summary>
    private async Task<string> BackupFileAsync()
    {
        await using var db = new TestDatabase(new Dictionary<string, string?>
        {
            ["Storage:Path"] = Path.Combine(_data, "uploads"),
            ["DataProtection:KeysPath"] = Path.Combine(_data, "keys"),
            ["Backups:Path"] = Path.Combine(_data, "elsewhere"),
        });
        await BackupSeed.SeedAsync(db, Path.Combine(_data, "uploads"), Path.Combine(_data, "uploads"), Path.Combine(_data, "keys"));
        return (await db.Get<IBackupWriter>().WriteAsync(new BackupRequest(), default)).Path;
    }

    private static string Key(Func<Task> action) => Assert.ThrowsAnyAsync<KeyedException>(action).GetAwaiter().GetResult().Key;

    /// <summary>An upload of a file, its stream closed again (an open one would keep the test's folder from being removed on Windows).</summary>
    private static async Task<RestorePoint> Upload(FileBackupStore store, string file, string? name, long maxBytes = long.MaxValue)
    {
        await using var content = File.OpenRead(file);
        return await store.SaveUploadAsync(content, name, maxBytes, default);
    }

    /// <summary>A copy of a backup whose manifest leaves out one field (edited by hand, say); the inner archive, and so the checksum, untouched.</summary>
    private static void CopyWithout(string backup, string field, string copy)
    {
        File.Copy(backup, copy);
        using var zip = ZipFile.Open(copy, ZipArchiveMode.Update);
        var entry = zip.GetEntry(BackupManifest.FileName)!;
        JsonObject manifest;
        using (var reader = new StreamReader(entry.Open())) manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        Assert.True(manifest.Remove(field));
        entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(BackupManifest.FileName).Open());
        writer.Write(manifest.ToJsonString());
    }

    [Fact]
    public async Task TheRestorePoints_AreEveryReadableBackup_NewestFirst_AndNothingElseInTheFolder()
    {
        var backup = await BackupFileAsync();
        Directory.CreateDirectory(Backups);
        File.Copy(backup, Path.Combine(Backups, "copied-by-hand.zip"));
        File.Copy(backup, Path.Combine(Backups, ".hidden.zip")); // no download or restore takes a hidden name, so it is not listed either
        await File.WriteAllTextAsync(Path.Combine(Backups, "notes.zip"), "not a zip at all");
        await File.WriteAllTextAsync(Path.Combine(Backups, "half.zip.partial"), "a backup being written");
        await File.WriteAllTextAsync(Path.Combine(Backups, "restore.refused.20261011T000000Z"), "{}");
        var store = Store();

        var point = Assert.Single(store.List());

        Assert.Equal(("copied-by-hand.zip", new FileInfo(backup).Length, false), (point.File, point.Bytes, point.Uploaded));
        Assert.Equal(Path.GetFullPath(Backups), store.Folder);
        Assert.Empty(new FileBackupStore(new FixedMonitor<BackupOptions>(new BackupOptions { Path = Path.Combine(_data, "no-such-folder") }), new NoDatabase()).List());
    }

    [Fact]
    public async Task AHold_IsAFileBesideTheBackup_ThatTheListReads_ThatNothingTakesForABackup_AndThatGoesWithIt()
    {
        var backup = await BackupFileAsync();
        Directory.CreateDirectory(Backups);
        File.Copy(backup, Path.Combine(Backups, "a.zip"));
        File.Copy(backup, Path.Combine(Backups, "b.zip"));
        var store = Store();
        var since = new DateTimeOffset(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

        store.Hold("a.zip", since);

        Assert.Equal(since, store.Find("a.zip").HeldSince);
        Assert.Equal([("a.zip", true), ("b.zip", false)], store.List().Select(p => (p.File, p.Held)).Order());
        Assert.True(File.Exists(Path.Combine(Backups, "a.zip.hold")));
        Assert.Equal("backup.notFound", Assert.ThrowsAny<KeyedException>(() => store.OpenRead("a.zip.hold")).Key); // never a backup itself
        Assert.Equal("backup.notFound", Assert.ThrowsAny<KeyedException>(() => store.Hold("../a.zip", since)).Key);

        // An empty one, as an operator's touch makes it: held since its file's time.
        await File.WriteAllTextAsync(Path.Combine(Backups, "b.zip.hold"), "");
        Assert.True(store.Find("b.zip").Held);
        // One without its time: since its file's time too, never the year 1.
        await File.WriteAllTextAsync(Path.Combine(Backups, "b.zip.hold"), "{}");
        Assert.Equal(new DateTimeOffset(File.GetLastWriteTimeUtc(Path.Combine(Backups, "b.zip.hold")), TimeSpan.Zero), store.Find("b.zip").HeldSince);

        store.Hold("a.zip", null);
        Assert.False(store.Find("a.zip").Held);
        store.Delete("b.zip");
        Assert.False(File.Exists(Path.Combine(Backups, "b.zip.hold"))); // gone with its backup, so a later copy under that name is not held
    }

    [Fact]
    public async Task AnUpload_IsStoredUnderANameOfItsOwn_ThatNeverReplacesAFile()
    {
        var backup = await BackupFileAsync();
        var store = Store();

        var first = await Upload(store, backup, "My backup (old)!.zip");
        var second = await Upload(store, backup, "My backup (old)!.zip");
        var nameless = await Upload(store, backup, null);
        var sneaky = await Upload(store, backup, "../../etc/passwd");

        Assert.Equal(["uploaded-My-backup--old.zip", "uploaded-My-backup--old-2.zip", "uploaded-backup.zip", "uploaded-passwd.zip"], new[] { first.File, second.File, nameless.File, sneaky.File });
        Assert.All(new[] { first, second, nameless, sneaky }, p => Assert.True(p.Uploaded));
        Assert.Equal(4, store.List().Count);
        Assert.Equal(await File.ReadAllBytesAsync(backup), await File.ReadAllBytesAsync(Path.Combine(Backups, first.File)));
        Assert.DoesNotContain(Directory.GetFiles(Backups), f => f.EndsWith(".uploading"));
    }

    [Fact]
    public async Task AnUploadThatIsTooLarge_OrNoBackup_OrDamaged_IsRefused_AndLeavesNothingBehind()
    {
        var backup = await BackupFileAsync();
        var store = Store();
        var damaged = await File.ReadAllBytesAsync(backup);
        damaged[300] ^= 0xff; // inside the inner archive: the checksum catches it
        // A file whose manifest reads well but names another writer.
        var foreign = new MemoryStream();
        using (var zip = new ZipArchive(foreign, ZipArchiveMode.Create, leaveOpen: true))
            using (var writer = new StreamWriter(zip.CreateEntry(BackupManifest.FileName).Open()))
                writer.Write((BackupFile.ReadManifest(backup) with { WrittenBy = "another-tool" }).ToJson());
        foreign.Position = 0;

        Assert.Equal("backup.tooLarge", Key(() => Upload(store, backup, "big.zip", 1000)));
        Assert.Equal("backup.corrupt", Key(() => store.SaveUploadAsync(new MemoryStream(RandomNumberGenerator.GetBytes(5000)), "noise.zip", long.MaxValue, default)));
        Assert.Equal("backup.corrupt", Key(() => store.SaveUploadAsync(new MemoryStream(damaged), "damaged.zip", long.MaxValue, default)));
        Assert.Equal("backup.corrupt", Key(() => store.SaveUploadAsync(foreign, "foreign.zip", long.MaxValue, default)));
        Assert.Empty(Directory.GetFiles(Backups));
    }

    [Fact]
    public async Task AnUploadOfANameAnotherUploadIsWriting_TakesTheNextNumber_AndLeavesThatFileAlone()
    {
        var backup = await BackupFileAsync();
        Directory.CreateDirectory(Backups);
        var another = Path.Combine(Backups, "uploaded-x.zip.uploading");
        await File.WriteAllTextAsync(another, "another upload, under way");
        var store = Store();

        var point = await Upload(store, backup, "x.zip");

        Assert.Equal("uploaded-x-2.zip", point.File);
        Assert.Equal("another upload, under way", await File.ReadAllTextAsync(another));
    }

    [Fact]
    public async Task UploadsOfOneName_AtOnce_AreEachStoredUnderANameOfTheirOwn()
    {
        var bytes = await File.ReadAllBytesAsync(await BackupFileAsync());
        var store = Store();

        // Each in its own task: none waits for the others to claim a name, so they race for the same one.
        var points = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.SaveUploadAsync(new MemoryStream(bytes), "same.zip", long.MaxValue, default))));

        Assert.Equal(8, points.Select(p => p.File).Distinct().Count());
        Assert.Equal(8, store.List().Count);
        Assert.All(points, p => Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(Backups, p.File))));
        Assert.DoesNotContain(Directory.GetFiles(Backups), f => f.EndsWith(".uploading"));
    }

    [Fact]
    public async Task AManifestWithoutTheReleaseOrTheDatabaseItCameFrom_IsNoBackup_NeitherListedNorTakenAsAnUpload()
    {
        var backup = await BackupFileAsync();
        Directory.CreateDirectory(Backups);
        CopyWithout(backup, "appVersion", Path.Combine(Backups, "no-version.zip"));
        CopyWithout(backup, "provider", Path.Combine(Backups, "no-provider.zip"));
        var store = Store();

        Assert.Empty(store.List());
        Assert.Equal("backup.corrupt", Key(() => Upload(store, Path.Combine(Backups, "no-version.zip"), "no-version.zip")));
        Assert.Equal("backup.corrupt", Key(() => Upload(store, Path.Combine(Backups, "no-provider.zip"), "no-provider.zip")));
        Assert.Equal(["no-provider.zip", "no-version.zip"], Directory.GetFiles(Backups).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task ADownload_IsAFileOfTheFolderByItsBareName_AndNothingElse()
    {
        var backup = await BackupFileAsync();
        Directory.CreateDirectory(Backups);
        File.Copy(backup, Path.Combine(Backups, "a.zip"));
        await File.WriteAllTextAsync(Path.Combine(_data, "secret.txt"), "outside the folder");
        await File.WriteAllTextAsync(Path.Combine(Backups, ".hidden"), "hidden");
        File.Copy(backup, Path.Combine(Backups, ".hidden.zip"));
        // The rest of the folder: never a backup, whatever its name says. The marker holds the identity of the backups in plain.
        new RestoreMarker("a.zip", Guid.NewGuid(), DateTimeOffset.UtcNow, "AGE-SECRET-KEY-1ZZZZ").Write(Backups);
        await File.WriteAllTextAsync(Path.Combine(Backups, "notes.zip"), "not a zip at all");
        File.Copy(backup, Path.Combine(Backups, "b.zip.partial"));
        File.Copy(backup, Path.Combine(Backups, "uploaded-c.zip.uploading"));
        File.Copy(backup, Path.Combine(Backups, "plain-copy"));
        var store = Store();

        await using (var stream = store.OpenRead("a.zip"))
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            Assert.Equal(await File.ReadAllBytesAsync(backup), copy.ToArray());
        }
        foreach (var name in new[] { "../secret.txt", "..", ".hidden", ".hidden.zip", "", "missing.zip", Path.Combine(_data, "secret.txt"),
                     RestoreMarker.FileName, "notes.zip", "b.zip.partial", "uploaded-c.zip.uploading", "plain-copy" })
            Assert.Equal("backup.notFound", Assert.Throws<NotFoundException>(() => store.OpenRead(name)).Key);
    }

    [Fact]
    public void ARestore_IsAskedFor_OnlyWhileItsBackupIsStillInTheFolder()
    {
        Directory.CreateDirectory(Backups);
        var store = Store();
        var marker = new RestoreMarker("a.zip", Guid.Empty, DateTimeOffset.UtcNow, null);

        // Deleted since its manifest was read: no marker, so no restart only to have the start refuse it.
        Assert.Equal("backup.notFound", Assert.Throws<NotFoundException>(() => store.RequestRestore(marker)).Key);
        Assert.False(store.RestorePending);

        File.WriteAllText(Path.Combine(Backups, "a.zip"), "there");
        store.RequestRestore(marker);
        Assert.True(store.RestorePending);
    }
}

public class BackupKeyMakerTests
{
    [Fact]
    public void AMadeUpPassphrase_Is100RandomBits_InFiveGroupsOfLettersAndDigitsThatCannotBeMistaken()
    {
        var phrases = Enumerable.Range(0, 50).Select(_ => BackupKeyMaker.NewPassphrase()).ToList();

        Assert.All(phrases, p => Assert.Matches(new Regex("^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){4}$"), p));
        Assert.Equal(phrases.Count, phrases.Distinct().Count());
    }

    [Fact]
    public void AKeySet_OpensWhatItsRecipientEncrypts_AndAShortPassphraseIsRefused()
    {
        var maker = new BackupKeyMaker(new FixedMonitor<BackupOptions>(new BackupOptions()));

        var made = maker.Make(null);
        var typed = maker.Make("correct horse battery staple");

        Assert.True(made.Generated);
        Assert.False(typed.Generated);
        Assert.Equal("correct horse battery staple", typed.Passphrase);
        foreach (var set in new[] { made, typed })
        {
            Assert.True(BackupKeys.Opens(AgeKeys.ParseIdentity(set.Identity), set.Recipient));
            Assert.Equal(16, Convert.FromBase64String(set.Salt).Length);
            Assert.Equal(AgeKeys.ParseIdentity(set.Identity), BackupKeys.Identity(set.Passphrase, Convert.FromBase64String(set.Salt), Argon2idParameters.Default)); // the passphrase alone derives it again
        }
        Assert.NotEqual(made.Salt, typed.Salt);
        var refused = Assert.Throws<DomainException>(() => maker.Make("short"));
        Assert.Equal(("backup.passphraseTooShort", 12), (refused.Key, refused.Args["min"]));
    }

    [Fact]
    public void ATypedPassphrase_IsJudgedByTheMinimumTheSettingsHoldNow_AMadeUpOneNever()
    {
        var options = new BackupOptions { PassphraseMinLength = 30 };
        var maker = new BackupKeyMaker(new FixedMonitor<BackupOptions>(options));

        var refused = Assert.Throws<DomainException>(() => maker.Make("correct horse battery staple")); // 28 characters
        Assert.Equal(("backup.passphraseTooShort", (object?)30), (refused.Key, refused.Args["min"]));
        Assert.True(maker.Make(null).Generated); // 20 characters of 100 random bits: the minimum is for what people type
        Assert.False(maker.Make("correct horse battery staple, twice").Generated);
    }

    [Fact]
    public void FromAPassphrase_TheKeySetIsMadeWithoutReadingTheSettings_WhichTheCommandLineChecksItself()
    {
        // The settings would not validate (a monitor that throws on read): keygen must work under them (#313).
        var maker = new BackupKeyMaker(new ThrowingMonitor());

        var set = maker.FromPassphrase("short");

        Assert.False(set.Generated);
        Assert.Equal("short", set.Passphrase);
        Assert.True(BackupKeys.Opens(AgeKeys.ParseIdentity(set.Identity), set.Recipient));
        Assert.Equal(AgeKeys.ParseIdentity(set.Identity), BackupKeys.Identity("short", Convert.FromBase64String(set.Salt), Argon2idParameters.Default));
        Assert.NotEqual(set.Salt, maker.FromPassphrase("short").Salt);
    }

    private sealed class ThrowingMonitor : IOptionsMonitor<BackupOptions>
    {
        public BackupOptions CurrentValue => throw new OptionsValidationException("Backups", typeof(BackupOptions), ["not valid"]);
        public BackupOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<BackupOptions, string?> listener) => null;
    }
}

public sealed class BackupFactsTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), $"tankstat-facts-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_data)) Directory.Delete(_data, recursive: true);
    }

    [Fact]
    public async Task TheDatabase_SaysHowLargeItIs_AndTheMigrationItIsAt()
    {
        await using var db = new TestDatabase();
        var facts = db.Get<IDatabaseFacts>();
        await using var context = await db.Get<IDbContextFactory<AppDbContext>>().CreateDbContextAsync();

        Assert.True(await facts.SizeAsync(default) > 4096);
        Assert.Equal(context.Database.GetMigrations().Last(), await facts.LastMigrationAsync(default));
    }

    [Fact]
    public async Task TheFiles_AreCountedPerRoot_ThePhotosOnlyWhenTheyHaveARootOfTheirOwn()
    {
        void Write(string path, int bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }
        Write(Path.Combine(_data, "uploads", "users", "a", "1"), 100);
        Write(Path.Combine(_data, "uploads", "vehicles", "b", "picture", "2"), 50);
        Write(Path.Combine(_data, "photos", "vehicles", "b", "refuelings", "c", "3"), 7);

        var apart = await new StorageUsage(Options.Create(new StorageOptions { Path = Path.Combine(_data, "uploads"), PhotosPath = Path.Combine(_data, "photos") })).MeasureAsync(default);
        var together = await new StorageUsage(Options.Create(new StorageOptions { Path = Path.Combine(_data, "uploads") })).MeasureAsync(default);
        var nothing = await new StorageUsage(Options.Create(new StorageOptions { Path = Path.Combine(_data, "none") })).MeasureAsync(default);

        Assert.Equal(new FileUsage(150, 7), apart);
        Assert.Equal(new FileUsage(150, null), together);
        Assert.Equal(new FileUsage(0, null), nothing);
    }
}

/// <summary>Options that never change, where a reader takes an <see cref="IOptionsMonitor{TOptions}"/>.</summary>
internal sealed class FixedMonitor<T>(T value) : IOptionsMonitor<T>
{
    public T CurrentValue => value;
    public T Get(string? name) => value;
    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
