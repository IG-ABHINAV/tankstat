using System.Security.Cryptography;

namespace Tankstat.Application.Setup;

/// <summary>
/// The instance's one-time code, made at every start (an in-process restart too) and written to the server's log: whoever reads the log
/// may use it. It guards what has no administrator to ask: the setup while it waits (the first administrator, the acknowledgement with
/// sign-in off, restoring a backup into the empty instance), and with sign-in off a restore asked for from the app. 12 characters of a
/// 32-letter alphabet without 0/O and 1/I (60 bits, out of reach of guessing over HTTP, so a wrong try voids nothing: a lockout would let
/// anyone who reaches a waiting instance keep its setup locked), shown in groups of four; compared in fixed time, the hyphens, spaces
/// and case of the typed code ignored.
/// </summary>
public sealed class OneTimeCode
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0/O, 1/I
    private const int Length = 12;

    private readonly string _code = RandomNumberGenerator.GetString(Alphabet, Length);

    /// <summary>The code as the log shows it: <c>ABCD-EFGH-JKLM</c>.</summary>
    public string Value => $"{_code[..4]}-{_code[4..8]}-{_code[8..]}";

    public bool Matches(string? code)
    {
        if (code is null) return false;
        var typed = new string([.. code.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant)]);
        return CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(_code), System.Text.Encoding.ASCII.GetBytes(typed));
    }

    /// <summary>
    /// The log category of every line that writes the code, below which <see cref="SetupLine"/> and <see cref="RestoreLine"/> are named: the
    /// console has them (the code is in the log on purpose), the OpenTelemetry exporter never (a telemetry backend's readers are not
    /// the log's), which a filter on this category keeps out.
    /// </summary>
    public static readonly string LogCategory = typeof(OneTimeCode).FullName!;

    /// <summary>The category of the line that says the setup waits for the code (<c>SetupService</c>).</summary>
    public sealed class SetupLine;

    /// <summary>The category of the line that names the code a restore asked for from the app needs with sign-in off (<c>RestoreStep</c>).</summary>
    public sealed class RestoreLine;
}
