using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Tankstat.Application.Backups;
using Tankstat.Domain.Backups;

namespace Tankstat.Infrastructure.Backups;

/// <summary>
/// A key set for encrypted backups, the same as <c>tankstat backup keygen</c> makes: Argon2id of the passphrase and a new salt. A passphrase
/// that is made up is 20 characters in five groups (<c>K7QM-4XWD-...</c>, from 32 letters and digits that cannot be mistaken for each other:
/// 100 random bits); one the administrator types needs at least <c>Backups:PassphraseMinLength</c> characters (<see cref="BackupPassphrase"/>),
/// read afresh: the Backups group applies at once.
/// </summary>
internal sealed class BackupKeyMaker(IOptionsMonitor<BackupOptions> options) : IBackupKeyMaker
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I

    public BackupKeySet Make(string? passphrase)
    {
        var generated = string.IsNullOrWhiteSpace(passphrase);
        var phrase = generated ? NewPassphrase() : passphrase!;
        if (!generated) BackupPassphrase.Validate(phrase, options.CurrentValue.PassphraseMinLength);
        return Derive(phrase, generated);
    }

    public BackupKeySet FromPassphrase(string passphrase) => Derive(passphrase, generated: false);

    private static BackupKeySet Derive(string phrase, bool generated)
    {
        var salt = BackupKeys.NewSalt();
        var identity = BackupKeys.Identity(phrase, salt, Argon2idParameters.Default);
        return new BackupKeySet(phrase, generated, AgeKeys.FormatIdentity(identity), AgeKeys.FormatRecipient(BackupKeys.Recipient(identity)), Convert.ToBase64String(salt));
    }

    internal static string NewPassphrase() =>
        string.Join('-', Enumerable.Range(0, 5).Select(_ => RandomNumberGenerator.GetString(Alphabet, 4)));
}
