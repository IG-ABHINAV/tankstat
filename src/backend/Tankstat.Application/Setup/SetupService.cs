using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Tankstat.Application.Access;
using Tankstat.Application.Auth;
using Tankstat.Application.Backups;
using Tankstat.Application.SystemSettings;
using Tankstat.Application.Users;
using Tankstat.Domain;
using Tankstat.Domain.Instance;
using Tankstat.Domain.Users;

namespace Tankstat.Application.Setup;

/// <summary>Whether the setup waits: public (the setup page and its notice), nothing secret in it.</summary>
public sealed record SetupInfo(bool Pending);

/// <summary>What the setup's last step did: the administrator it made (Standalone), and whether the server restarts now for the settings saved.</summary>
public sealed record SetupFinished(User? Administrator, bool Restarting);

/// <summary>
/// The setup of a new instance. A new instance waits until it has its gatekeeper: its first administrator (Standalone, OIDC, proxy), or
/// with sign-in off the acknowledgement that everyone who reaches it may see and change everything. While it waits, the one-time code
/// from the server's log (<see cref="OneTimeCode"/>) opens the setup page's steps (<see cref="SetupGate"/>), since no administrator exists
/// to ask: the settings (the wizard saves them through <see cref="SystemSettingsService"/> with the code), then <see cref="FinishAsync"/>;
/// once it is completed nothing here opens again, unless a start in Standalone finds no administrator and reopens it (#149, see
/// <see cref="StartAsync"/>). What the environment already says completes it at start (<see cref="StartAsync"/>), so
/// an unattended deployment never sees the page (in Standalone, which no setting completes, it sends the last step itself with the code
/// from the log); a restore brings the backup's setup state along.
/// </summary>
public sealed class SetupService(
    ISetupStore store,
    SetupStatus status,
    SetupGate gate,
    OneTimeCode code,
    AuthService authService,
    AccessService access,
    SystemSettingsService settings,
    ISystemConfiguration configuration,
    IServerRestart restart,
    IUserRepository users,
    IBackupStore backups,
    ISettingsBackup settingsFile,
    IDatabaseFacts database,
    IOptionsSnapshot<BackupOptions> backupOptions,
    IOptions<AuthOptions> auth,
    IOptions<SetupOptions> options,
    TimeProvider clock,
    ILogger<SetupService> logger,
    ILogger<OneTimeCode.SetupLine> codeLogger)
{
    /// <summary>
    /// The settings that made the first administrator before the setup page was the only way (#149): no longer read, so a start that still
    /// finds them says so, by name only.
    /// </summary>
    public static readonly IReadOnlyList<string> RetiredSettings = ["Auth:Standalone:AdminEmail", "Auth:Standalone:AdminPassword"];

    /// <summary>
    /// At every start, after a waiting restore and the migrations: decides a setup that is undecided (a database from before setup existed
    /// is set up when it holds data, a new one waits). Standalone without an enabled local administrator needs the setup page, the only one
    /// that makes the first administrator (#149): a set-up instance in that state (the mode changed outside the app, or every administrator
    /// gone) reopens its setup, and with the setup turned off (<c>Setup:Enabled=false</c>) the start stops. A setup reopened that way is
    /// completed again as it was by a start in another mode (the mode switched back: the way back is the environment). Then a waiting setup
    /// the environment completes is completed (the setup turned off, a local administrator there, <c>Auth:AdminEmails</c>). While it waits,
    /// the code is logged.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        WarnOfRetiredSettings();
        var phase = await store.PhaseAsync(ct);
        if (phase == SetupPhase.Undecided)
        {
            phase = await store.DecideAsync(await store.HoldsDataAsync(ct), clock.GetUtcNow(), ct);
            if (phase == SetupPhase.Completed) logger.LogInformation("The database holds data from before the setup page existed: the instance counts as set up");
        }
        if (auth.Value.Mode == AuthMode.Standalone && !await users.AnyLocalAdminAsync(ct))
        {
            if (!options.Value.Enabled)
                throw new InvalidOperationException(
                    "Auth:Mode is Standalone but no administrator exists, and the setup is turned off (Setup:Enabled=false; Setup__Enabled in the environment). Only the setup page makes the first administrator: turn the setup on, start again and finish it at /setup with the one-time code from the log.");
            if (phase == SetupPhase.Completed)
            {
                // Where this write finds the setup pending already, another server starting at the same moment reopened it.
                if (await store.ReopenAsync(clock.GetUtcNow(), ct))
                    logger.LogWarning("The setup is reopened: Standalone has no administrator. Until it is finished on the setup page, local users who are not administrators cannot sign in");
                phase = await store.PhaseAsync(ct);
            }
        }
        else if (phase == SetupPhase.Pending)
        {
            if (auth.Value.Mode != AuthMode.Standalone && await store.CompleteReopenedAsync(clock.GetUtcNow(), ct))
            {
                logger.LogInformation("The setup a start in Standalone reopened is completed again as it was: the instance runs {Mode}", auth.Value.Mode);
                phase = SetupPhase.Completed;
            }
            else if (CompletedBy() is { } reason)
            {
                // Completed either way: where this write finds it completed, another server starting at the same moment was first.
                if (await store.CompleteAsync(SetupCompletion.Environment, clock.GetUtcNow(), ct))
                    logger.LogInformation("The setup is completed by the environment: {Reason}", reason);
                phase = SetupPhase.Completed;
            }
        }

        status.Set(phase == SetupPhase.Pending);
        // A Warning, so a log kept to warnings still has it: without it the instance cannot be set up from the browser. Its own category keeps
        // it off the telemetry exporter (OneTimeCode.LogCategory).
        if (phase == SetupPhase.Pending)
            codeLogger.LogWarning("Setup is waiting: open /setup in the browser and enter the one-time setup code {Code}", code.Value);
    }

    /// <summary>
    /// What in the environment completes a waiting setup, by the setting's name; null when nothing does. Standalone gets here only with an
    /// enabled local administrator: without one the start reopened the setup, or stopped.
    /// </summary>
    private string? CompletedBy()
    {
        if (!options.Value.Enabled) return "Setup:Enabled is false";
        return auth.Value.Mode switch
        {
            AuthMode.Standalone => "a local administrator exists",
            AuthMode.Oidc or AuthMode.ProxyHeader => auth.Value.AdminEmails.Any(e => !string.IsNullOrWhiteSpace(e)) ? "Auth:AdminEmails" : null,
            _ => null,
        };
    }

    /// <summary>One Warning per start for each retired setting still set (in the environment or the settings file), never its value.</summary>
    private void WarnOfRetiredSettings()
    {
        var stillSet = RetiredSettings.Where(key => !string.IsNullOrEmpty(configuration.Running[key])).ToList();
        if (stillSet.Count > 0)
            logger.LogWarning("{Settings} are no longer used: the first administrator is made on the setup page. Remove them from the environment or the settings file",
                string.Join(", ", stillSet));
    }

    /// <summary>Whether the setup waits, asked of the database while this process thinks so (another server may have completed it).</summary>
    public async Task<SetupInfo> InfoAsync(CancellationToken ct) => new(await gate.WaitsAsync(ct));

    /// <summary>The restore points of the backups folder, to restore one into the waiting instance; with the recipient new backups are encrypted for.</summary>
    public async Task<(RestorePoints Points, string? CurrentRecipient)> RestorePointsAsync(string? typedCode, CancellationToken ct)
    {
        await gate.RequireAsync(typedCode, ct);
        var points = await RestorePoints.ListAsync(backups, settingsFile, database, ct);
        return (points, backupOptions.Value.CurrentRecipient);
    }

    /// <summary>One backup of the folder in full, to look into before restoring it; with the recipient new backups are encrypted for.</summary>
    public async Task<(RestorePoint Point, string? CurrentRecipient)> BackupDetailsAsync(string? typedCode, string fileName, CancellationToken ct)
    {
        await gate.RequireAsync(typedCode, ct);
        return (backups.Find(fileName), backupOptions.Value.CurrentRecipient);
    }

    /// <summary>The largest backup the setup page may upload (`Backups:MaxUploadMb`), which the Backups page tells administrators only.</summary>
    public async Task<int> UploadLimitMbAsync(string? typedCode, CancellationToken ct)
    {
        await gate.RequireAsync(typedCode, ct);
        return backupOptions.Value.MaxUploadMb;
    }

    /// <summary>
    /// The setup's last step, once the wizard saved its settings: the gatekeeper of the sign-in mode the next start runs, then the one restart
    /// the saved settings wait for. Standalone makes the first administrator, the only way to one (#149: a valid address, a password
    /// within the policy, typed twice: <c>password.mismatch</c> before any administrator is looked up or made) and completes the setup with it, in one transaction;
    /// sign-in off needs the acknowledgement that everyone who reaches
    /// the instance may see and change everything; OIDC and proxy complete nothing here: the administrators the settings name complete it as
    /// the server starts, else the signed-in user claims it after the restart (<see cref="ClaimAdministratorAsync"/>, the same code). What the
    /// next start would refuse refuses this step before anything is completed.
    /// </summary>
    public async Task<SetupFinished> FinishAsync(
        string? typedCode, string? email, string? password, string? passwordConfirmation, bool acknowledgeOpenAccess, CancellationToken ct)
    {
        await gate.RequireAsync(typedCode, ct);
        var restarting = await settings.RestartPendingAsync(ct);
        if (restarting) await settings.RequireStartableAsync(ct);
        User? administrator = null;
        switch (NextMode())
        {
            case AuthMode.Standalone:
                PasswordPolicy.RequireRepeated(password, passwordConfirmation);
                if (await users.AnyLocalAdminAsync(ct)) throw new DomainException("setup.administratorExists", "An administrator exists already: sign in.");
                administrator = authService.NewAdministrator(email, password);
                if (await users.FindLocalByEmailAsync(administrator.Email, ct) is not null) throw new DomainException("user.emailExists", "A user with this e-mail already exists.");
                AfterCompletion(await store.CompleteWithAdministratorAsync(administrator, clock.GetUtcNow(), ct));
                logger.LogInformation("The setup page made the first administrator {UserId}: the setup is completed", administrator.Id);
                break;
            case AuthMode.None:
                if (!acknowledgeOpenAccess)
                    throw new DomainException("setup.acknowledgementRequired", "With sign-in off, the setup ends with the acknowledgement that everyone who reaches the instance sees and changes everything.");
                AfterCompletion(await store.CompleteAsync(SetupCompletion.Acknowledged, clock.GetUtcNow(), ct));
                logger.LogInformation("Open access was acknowledged on the setup page (sign-in is off): the setup is completed");
                break;
        }
        if (restarting)
        {
            logger.LogInformation("The setup page restarts the server for the settings it saved");
            restart.Restart();
        }
        return new SetupFinished(administrator, restarting);
    }

    /// <summary>The sign-in mode the next start runs: what the saved settings say beside the environment's (the running one waits for a restart).</summary>
    private AuthMode NextMode() => Enum.TryParse<AuthMode>(configuration.Running["Auth:Mode"], ignoreCase: true, out var mode) ? mode : AuthMode.None;

    /// <summary>
    /// OIDC and proxy mode: the signed-in user (provisioned by their sign-in, so anyone the provider or proxy lets in) becomes the first
    /// administrator, which completes the setup; the code is the whole guard, since no administrator exists to ask.
    /// </summary>
    public async Task<Principal> ClaimAdministratorAsync(string? typedCode, CancellationToken ct)
    {
        await gate.RequireAsync(typedCode, ct);
        RequireMode(AuthMode.Oidc, AuthMode.ProxyHeader);
        var user = await access.RequirePrincipalAsync(ct);
        AfterCompletion(await store.CompleteWithClaimAsync(user.Id, clock.GetUtcNow(), ct));
        logger.LogInformation("User {UserId} became the first administrator on the setup page: the setup is completed", user.Id);
        return user with { IsAdmin = true };
    }

    private void RequireMode(params AuthMode[] modes)
    {
        if (!modes.Contains(auth.Value.Mode))
            throw new DomainException("setup.otherMode", $"This step is not part of the setup in {auth.Value.Mode} sign-in mode.", new { Mode = auth.Value.Mode.ToString() });
    }

    /// <summary>
    /// After a step's conditional completion: the setup is completed either way, so this process's copy follows; when the write found it
    /// completed, another request (or server) was first and the step is refused.
    /// </summary>
    private void AfterCompletion(bool completedHere)
    {
        status.Set(false);
        if (!completedHere) throw SetupGate.Completed();
    }
}
