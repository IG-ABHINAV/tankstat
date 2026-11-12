using Tankstat.Application.Backups;

namespace Tankstat.Application.UnitTests;

/// <summary>What the backup commands do, behind the Application's ports (#313); the command line itself is pinned by <c>BackupCommandsTests</c>.</summary>
public class BackupCommandServiceTests
{
    private static readonly BackupKeySet Key = new("a passphrase", false, "AGE-SECRET-KEY-1X", "age1recipient", "c2FsdA==");

    private sealed class Keys : IBackupKeyMaker
    {
        public List<string> Derived { get; } = [];
        public BackupKeySet Make(string? passphrase) => throw new InvalidOperationException("The command line never makes a passphrase up.");
        public BackupKeySet FromPassphrase(string passphrase)
        {
            Derived.Add(passphrase);
            return Key;
        }
    }

    private sealed class Writer : IBackupWriter
    {
        public BackupRequest? Asked { get; private set; }
        public Task<BackupResult> WriteAsync(BackupRequest request, CancellationToken ct)
        {
            Asked = request;
            return Task.FromResult(new BackupResult("out.zip", null!));
        }
    }

    private sealed class Archive : IBackupArchive
    {
        public List<string> Steps { get; } = [];
        public BackupManifest Manifest { get; } = null!;
        public BackupManifest ReadManifest(string path) { Steps.Add($"manifest {path}"); return Manifest; }
        public void VerifyChecksum(string path, BackupManifest manifest) => Steps.Add("checksum");
        public byte[] IdentityOf(BackupManifest manifest, string passphrase) { Steps.Add($"identity of {passphrase}"); return [1, 2, 3]; }
        public void Unpack(string path, BackupManifest manifest, byte[]? identity, string folder) =>
            Steps.Add($"unpack into {folder} with {(identity is null ? "none" : string.Join(",", identity))}");
    }

    private sealed class Startup(RestoreOutcome outcome) : IStartupRestore
    {
        public int Runs { get; private set; }
        public Task<RestoreOutcome> RestoreAndMigrateAsync(CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(outcome);
        }
    }

    [Fact]
    public async Task ABackupForANewPassphrase_IsEncryptedForItsKey_AndOneWithoutAsTheSettingsSay()
    {
        var keys = new Keys();
        var writer = new Writer();
        var service = new BackupCommandService(keys, writer, new Archive(), new Startup(RestoreOutcome.None));

        await service.WriteAsync(new BackupRequest("file.zip", Photos: true), "typed twice", default);
        Assert.Equal(["typed twice"], keys.Derived);
        Assert.Equal(new BackupEncryption(BackupEncryption.AgeX25519, Key.Recipient, Key.Salt, Argon2idParameters.Default), writer.Asked!.Encryption);
        Assert.Equal(("file.zip", (bool?)true), (writer.Asked.Output, writer.Asked.Photos));

        await service.WriteAsync(new BackupRequest(), null, default);
        Assert.Null(writer.Asked!.Encryption);
        Assert.Single(keys.Derived);
        Assert.Same(Key, service.Keygen("another"));
    }

    [Fact]
    public void Decrypting_ReadsTheManifestAndTheChecksumsBeforeTheIdentityIsAskedFor()
    {
        var archive = new Archive();
        var service = new BackupCommandService(new Keys(), new Writer(), archive, new Startup(RestoreOutcome.None));

        service.Decrypt("b.zip", "out", m => { archive.Steps.Add("asked"); return service.IdentityOf(m, "pass"); });
        service.Decrypt("c.zip", "plain", _ => null);

        Assert.Equal(["manifest b.zip", "checksum", "asked", "identity of pass", "unpack into out with 1,2,3", "manifest c.zip", "checksum", "unpack into plain with none"], archive.Steps);
    }

    [Fact]
    public async Task RestoringNow_RunsTheStartsStepsOnce_AndSaysHowItWent()
    {
        var startup = new Startup(RestoreOutcome.Refused);
        var service = new BackupCommandService(new Keys(), new Writer(), new Archive(), startup);

        Assert.Equal(RestoreOutcome.Refused, await service.RestoreNowAsync(default));
        Assert.Equal(1, startup.Runs);
    }
}
