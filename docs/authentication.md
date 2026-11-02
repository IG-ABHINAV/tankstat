# Authentication and access control

For operators: how users sign in (none, standalone, OpenID Connect or a trusted proxy), every setting of each mode, e-mail for password links, and who may see and change what.

Authentication is configured through the `Auth` and `Smtp` sections. Every setting can be given in `appsettings.json`, in [the app's own settings file](configuration.md#the-apps-own-settings-file) or as an **environment variable**, where `:` becomes `__` (double underscore): `Auth:Oidc:ClientId` is `Auth__Oidc__ClientId`. Environment variables override both files. List settings use an index: `Auth__AdminEmails__0=a@x.com`, `Auth__AdminEmails__1=b@x.com`; a list is merged item by item, so the environment replaces only the items at the indexes it sets (remove a list from the settings file to override it from the environment). Names are case-insensitive, and enum values (`Auth__Mode`) are matched case-insensitively. A misconfiguration in the environment stops the app at start with a message naming the missing setting; one in the app's own settings file leaves that file out instead (the app starts without it, with the `SETTINGS_FILE_IGNORED` notice and an `Error` line naming the setting), unless leaving it out would turn authentication off: a file that sets the mode while the environment does not, over the shipped `None`, stops the start when it cannot be used (see [what that means and its limits](configuration.md#the-apps-own-settings-file)).

> **The shipped `appsettings.json` sets `Auth:Mode=None`: there is no authentication, everyone sees and changes everything, and there is no administrator.** Always set `Auth__Mode` explicitly (and, for `Standalone`, finish the [setup](#the-first-start-setup), which makes the first administrator) before exposing the app. Never put real credentials in a committed file.

## Choosing the mode

`Auth__Mode` selects how users are authenticated. Default when nothing is configured: `None`.

| `Auth__Mode` value | Who authenticates | Required settings (see the tables below) | Notes |
| --- | --- | --- | --- |
| `None` | nobody | none | **Unsafe.** Everyone sees and changes everything; the UI shows a warning. Data belongs to an anonymous owner; there are no profile pictures, no administration. A sign-in left over from another mode (a browser still holding its cookies after the switch) counts for nothing: every visitor is the anonymous owner. |
| `Standalone` | the app itself | none: the [setup page](#the-first-start-setup) makes the first administrator, the only way to it (a script can finish it without a browser) | Login with e-mail and password, password change, one-time reset links, lockout, user management by administrators (create, edit name and e-mail, reset link, disable, delete). |
| `Oidc` | an OpenID Connect provider | `Auth__Oidc__Authority`, `Auth__Oidc__ClientId`, `Auth__Oidc__ClientSecret` | Server-side authorization-code flow with PKCE; the browser only gets the two HttpOnly cookies of a session (see *Sessions* below). Redirect URI to register: `https://<your-host>/auth/oidc/callback`. |
| `ProxyHeader` | a trusted reverse proxy (Authelia, Authentik, Cloudflare Access, oauth2-proxy, ...) | `Auth__ProxyHeader__TrustedProxies__0` (at least one) | The app trusts a user header, but only from the listed proxy addresses. There is no login or logout in the app. |

All modes end in the same place: a user record in the database that owns data.

## The first start: setup

A new instance (an empty database) **waits for its setup** until it has its first administrator, or, with sign-in off, until someone acknowledges that everyone who reaches it sees and changes everything. While it waits, the app writes a **one-time setup code** to its log as it starts, as a warning (so a log kept to warnings shows it too):

```
Setup is waiting: open /setup in the browser and enter the one-time setup code ABCD-EFGH-JKLM
```

With Docker, `docker logs tankstat` shows it. Every start of the server writes a new code (a restart from inside the app keeps it, so the setup goes on with the one you entered), so use the latest; case, spaces and hyphens do not matter. Whoever can read the server's log is the one the setup trusts: no administrator exists yet to ask. The code is never shown anywhere else.

Opening the app shows the setup page, which asks for the code and then takes you through the setup as a wizard, step by step:

1. **The settings**, a step per group of the *Server settings* page with the same fields (what the environment sets is shown read-only, and a group with nothing to choose has no step): the sign-in first, whose mode must be chosen (none is preselected, and sign-in off is one of the choices), then the e-mail, photo reading, backups, defaults, notifications, offline sync, logging and telemetry, each of which can be skipped. *Next* saves a step into the app's own settings file, *Back* goes to the one before.
2. **The last step**, by the sign-in mode chosen:
   - `Standalone`: an e-mail address and a password make the first administrator.
   - `None`: tick that you understand everyone who reaches the address sees and changes all data, and finish. Until then every page shows the setup, but the API itself is as open as sign-in-off always is.
   - `Oidc` and `ProxyHeader`: the administrators named in the sign-in step (`Auth:AdminEmails`) become administrators as they sign in. With none named, sign in through the provider or the proxy after the restart: you come back to the setup (enter the code again), and *Become the administrator* makes you it. Where the provider runs already, the visitor signs in first, then follows *Open the setup* in the notice.
3. **One restart**, when a saved setting waits for it (the sign-in mode always does): the server restarts in its own process, keeping the code, and the page goes on once it is back. A new sign-in mode signs everyone out, so the new administrator then signs in.

**Or restore a backup** instead, with *Restore from a backup* on the first step: only then are the backups of the server's backups folder listed, and *Start fresh instead* goes back. A backup the folder does not hold (one of the old server, moving to a new one) is uploaded right there with *Upload a backup…*, up to `Backups:MaxUploadMb`, and then listed with the others; a larger one is copied into the folder by hand, then *Look again* (see [Backup and restore](backup-and-restore.md)). The restored data brings its users, its own setup and its settings file (what the wizard saved before replaced, kept as `settings.json.bak`), so the instance is set up as the backup was, under this server's environment variables. In Standalone the backup must hold an administrator, as for any restore; only a backup of an instance that was itself still waiting for its setup needs neither, and the restored instance then waits for its setup in turn.

Once the setup is done, the page and the code open nothing again (unless a start in `Standalone` finds no administrator, below). The new administrator lands on what to set up next, none of it part of the setup: a key set for encrypted backups (made right there, as on the *Backups* page), then links to the backup schedule and the defaults for new vehicles on the *Server settings* page and, in `Standalone`, to *People* for the other users.

**The setup is the only way to a `Standalone` instance's first administrator.** An unattended deployment finishes it without a browser: read the code from the log and send one request, for example

```sh
password='a long password'
code="$(docker logs tankstat 2>&1 | grep -oE 'setup code [A-Z0-9-]{14}' | tail -n 1 | cut -d' ' -f3)"
curl -s -H 'Content-Type: application/json' http://localhost:8080/graphql \
  -d "{\"query\":\"mutation(\$i: SetupFinishInput!) { setupFinish(input: \$i) { restarting } }\",\"variables\":{\"i\":{\"code\":\"$code\",\"email\":\"admin@example.com\",\"password\":\"$password\",\"passwordConfirmation\":\"$password\"}}}"
```

Run it once the server answers (the start writes the line before that), and check the answer: `{"data":{"setupFinish":{"restarting":false}}}`, or `errors` saying what is wrong (a code that is not the latest start's, a password the rules refuse). The password goes in single quotes, so the shell leaves a `$` or a backtick in it alone (written into the double-quoted line itself, `$ecret` would silently become nothing); a `'` in it is written `'\''` there, and a `"` or `\` is written `\"` or `\\`, as JSON wants.

**What skips the setup**, so an unattended deployment of the other modes never needs it:

- `Oidc` and `ProxyHeader` with `Auth__AdminEmails__0`: whoever it names becomes the administrator by signing in.
- `Setup__Enabled=false` in `None`, `Oidc` and `ProxyHeader`, and in `Standalone` once an administrator exists. A `Standalone` instance without one stops at start with the setup turned off, since nothing else can make the administrator: turn the setup on and finish it.

**A database that already holds data** (users or vehicles, from before this release) counts as set up. **A set-up `Standalone` instance with no enabled administrator** (switched to `Standalone` in the environment or the settings file, or every administrator gone) **reopens its setup** at start: the same page, a new code in the log (with a warning that the setup was reopened), and the last step makes a new administrator (with an address no local account has: a former administrator's own account is enabled or made administrator again on *People* afterwards). Switched back to the mode it ran before instead, the instance is set up again as it was: the way back is the environment. Until it is finished, the setup page stands in for the sign-in, so the local users who are not administrators cannot sign in either. `Auth__Standalone__AdminEmail` and `Auth__Standalone__AdminPassword`, which made the first administrator in releases before 0.17, are no longer read: a start that still finds them says so in a warning (by name only), and they can be removed.

Several servers on one database each print their own code; the first to finish the setup completes it for all of them.

The mode can also be changed from inside the app, by an administrator through the system settings (written into [the app's own settings file](configuration.md#the-apps-own-settings-file); the environment keeps winning, so `Auth__Mode` set in a compose file shows as read-only there). The change takes effect at the next restart, which signs every device out (see *Sessions*). Two things are checked before it is saved, because a mistake there would lock everybody out: an OpenID Connect authority the next start would sign in through (a switch to `Oidc`, or another authority) must answer with its discovery document and name itself as the issuer, and `Standalone` is refused while no enabled local administrator exists (users cannot be created outside `Standalone`, so only the setup page could make one): switch to it outside the app instead, with `Auth__Mode` in the environment or the settings file, and restart the server from there (the app's own restart is refused too): that start opens the setup page, or with `Setup__Enabled=false` stops. The way back is always the environment: `Auth__Mode` beats the file, so set it and restart. In mode `None` there is no administrator, so a `None` instance changes its mode through the environment.

## Sessions (Standalone and OIDC)

A signed-in browser holds two HttpOnly cookies, so no script on the page ever sees a token:

- **`tankstat.session`, the access cookie** (`SameSite=Lax`, Secure on HTTPS): read on every request in these two modes, valid for `Auth__AccessTokenMinutes` (15 minutes), not sliding. It survives closing the browser (also after an OIDC sign-in), for those few minutes. After a switch to `None` or `ProxyHeader` a cookie still in the browser is never read and names nobody (in `None` every visitor is the anonymous owner, in `ProxyHeader` only the proxy's header names a user).
- **`tankstat.refresh`, the refresh cookie** (same attributes, but only sent to `/auth/token/...`): the refresh token of the device's session, valid for `Auth__RefreshTokenDays` (90 days) from its last use.

When the access cookie ran out, the app trades the refresh token at `POST /auth/token/refresh` for a new access cookie and a **new** refresh token, and sends the request again; the person notices nothing (also when the app is opened again later: before it shows the sign-in screen it tries the refresh cookie once). A device that is used at least once in 90 days stays signed in, which also lets the installed app come back after weeks. Each device's session is a row of `UserSessions` that stores only a hash of its secret (and the secret encrypted with the key ring, see below).

What ends a session (at once: the access cookie names its session, which is checked on every request like the user is):

- **Sign out** (`POST /auth/token/logout`, or the `logout` mutation): this device's session, even after its access cookie ran out. It also works while the server cannot be reached: the device's data goes and the sign-in screen shows at once, and the device tells the server once it can (a server that answers without ending the session is told again every 30 seconds), staying signed out until then; meanwhile the server still accepts that session's cookies, which only it can clear. A server that has no sessions to end any more (restarted with sign-in off, or behind a proxy) counts as told. Signing in on such a device: with a password, the server is told once more first, and the device forgets the old sign-out only once the new sign-in worked (a wrong password leaves it signed out); with an identity provider, *Sign in* goes to the provider only once the server ended the old session, and says so while it has not. A sign-in that bypasses that button (the provider's sign-in address opened directly) ends signed out once more, and *Sign in* then works.
- **The Account page's *Signed-in devices*** (`mySessions`, `revokeSession(id)`, `revokeOtherSessions`): you see every device you are signed in on (a coarse name worked out from the browser, such as "Firefox on Linux", never the raw User-Agent; when it signed in and was last used) and sign out one of them, or every one but this.
- **Changing your password**: every other device; the one that changed it stays signed in. **Resetting it with a link**, an **administrator setting it**, or **disabling the user**: every device.
- **A refresh token used again**: a token that was already traded in is accepted once more within `Auth__RefreshRotationGraceSeconds` (and answered with the current one, so tabs that refreshed at the same moment agree); used later, someone else must have a copy, so the session ends (a Warning in the log names it by id).
- **The instance starting in another authentication mode** than the one it last started in (from `Standalone` to `Oidc`, or back): every device of every user is signed out at that start, so a session issued in one mode never works in the next (the log says how many were signed out). A restart in the same mode signs nobody out.
- Not using the device for `Auth__RefreshTokenDays`. Ended and expired sessions are deleted a week later.

The token endpoints only answer requests with the header `X-Requested-With: fetch`, which a page of another site cannot send, and do not exist in `None` and `ProxyHeader` modes (there the proxy signs every request in itself). The cookies are protected by the key ring (`DataProtection:KeysPath`, see [Configuration](configuration.md)): keep it with the data, or every restart signs everybody out.

## General settings (every mode)

| Environment variable | Type / values | Default | Used in | Meaning |
| --- | --- | --- | --- | --- |
| `Auth__Mode` | `None`, `Standalone`, `Oidc`, `ProxyHeader` | `None` | all | The authentication mode. |
| `Auth__PublicUrl` | URL, e.g. `https://tankstat.example.com` | empty | `Standalone` | Public base URL of the app, used to build the password-setup link (`<PublicUrl>/?resetToken=...`). **Required when `Smtp__Host` and `Smtp__From` are set.** Without it, administrators get the token and hand the link over themselves. |
| `Auth__AccessTokenMinutes` | integer (1-1440) | `15` | `Standalone`, `Oidc` | How long the access cookie lasts; a device that is still signed in gets a new one silently. |
| `Auth__RefreshTokenDays` | integer (1-3650) | `90` | `Standalone`, `Oidc` | How long a device stays signed in **without being used**; every use starts the period again. |
| `Auth__RefreshRotationGraceSeconds` | integer (0-3600) | `60` | `Standalone`, `Oidc` | How long a refresh token the device already traded in is still accepted (an answer that never arrived, two tabs refreshing at once). |
| `Auth__AdminEmails__0`, `Auth__AdminEmails__1`, ... | list of e-mail addresses or user names | empty | `Oidc`, `ProxyHeader` | Identities that are made administrators when they sign in. Matched case-insensitively against the provider's subject **or** the e-mail. Configuration only promotes: demoting an administrator is done in the UI. |

## Standalone settings (`Auth__Mode=Standalone`)

| Environment variable | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `Auth__Standalone__PasswordMinLength` | integer, 10-128 | `10` | no | The least number of characters a new password must have. It is checked for every new password (a change, a reset or first password through a link, one an administrator sets, the setup's first administrator). Existing passwords keep working: a user whose password is shorter than a raised minimum signs in as before, nobody is signed out, and the app then shows only the change of the password (and Sign out) until it is done (`session.user.passwordChangeRequired`, set at each sign-in by the password typed, cleared by any new password: a change, a reset, an administrator's); the server holds nothing else back, so an installed app keeps syncing, and offline the change waits for the server like any sign-in. Unlike the rest of the sign-in settings, a change on the *Server settings* page applies at once, without a restart. The forms say the minimum before anything is typed (the public `passwordRequirements` query). A value outside 10-128 is refused, and the app does not start with one; one that stops being usable while the app runs (an `appsettings.json` edited by hand is read again on its own) is said once by a `Warning`, and the last usable minimum stays in force until it is fixed. |
| `Auth__Standalone__MaxFailedAttempts` | integer | `5` | no | Failed logins before the account is locked. |
| `Auth__Standalone__LockoutMinutes` | integer (minutes) | `15` | no | How long a locked account stays locked. |
| `Auth__Standalone__ResetTokenMinutes` | integer (minutes), 1-43200 | `60` | no | How long a password setup/reset link is valid (it can be used once). At most 30 days; the app does not start with a larger value. |
| `Auth__Standalone__ResetCooldownMinutes` | integer (minutes), 0-1440 | `5` | no | After a link was issued to an account, a request for another one through *Forgot your password?* sends nothing for this long, or until that link expired if that is sooner (the answer looks the same), so nobody can flood a mailbox with reset mails. A link requested this way whose e-mail could not be sent is taken back and does not count; one an administrator issued counts even when its e-mail failed, because the administrator was given that link to hand over. Requests at the same moment send one mail; with several app instances on one database, each instance can send one. `0` turns the limit off. Links an administrator issues are never held back. |
| `Auth__Standalone__AllowAdminSetPassword` | `true` / `false` | `false` | no | Lets administrators set a user's password directly in the UI (the user is signed out everywhere). Off by default: administrators then only issue reset links. |

Deleting a user: if they own vehicles or logs, the administrator chooses to move everything (including trashed items, authorship and sharing grants) to another user, or to delete it permanently. The last active administrator and your own account cannot be deleted. Name and e-mail of `Oidc`/`ProxyHeader` users come from the provider and are not editable.

Facts that matter when configuring: passwords need `PasswordMinLength` (10 unless set higher) to 128 characters; every new password is typed twice (a change, a reset or first password through a link, one an administrator sets, the setup's first administrator), and the server refuses one whose repetition differs or is missing (`password.mismatch`, in the API too: `newPasswordConfirmation`, `passwordConfirmation`) before it looks at anything else; changing or resetting a password signs out every other session; administrators create users in the UI, which issues a one-time setup link (e-mailed when `Smtp` is configured, otherwise shown to the administrator; when the e-mail could not be sent, the link is shown as well and a Warning in the log says why); users can request a reset link themselves when `Smtp` is configured (always answered the same way and just as fast, so accounts cannot be discovered: the e-mails go out after the answer, four at a time, so behind a mail server that does not answer a request waits up to 20 seconds for every four before it; a mail server that cannot be reached is only a Warning in the log, a request still waiting to be sent when the app stops is lost (the log says whose), so the user asks again, and one still waiting when the password is changed or reset sends nothing; at most one e-mail per `ResetCooldownMinutes` per account while its latest link is still valid, and without `Smtp` nothing is issued); each new link, also one an administrator issues, replaces the account's earlier ones once it was e-mailed or shown to the administrator, so only the latest link works (a link the user asked for whose e-mail could not be sent replaces nothing: a link an administrator handed over keeps working); how long a device stays signed in is described under *Sessions* below.

## OpenID Connect settings (`Auth__Mode=Oidc`)

| Environment variable | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `Auth__Oidc__Authority` | URL of the issuer, e.g. `https://id.example.com` | empty | yes | The provider's issuer URL (its `/.well-known/openid-configuration` is read from here). A non-HTTPS authority is accepted for local development only. Before the system settings save an authority, the app reads that document (within 10 seconds, without following a redirect) and refuses one that does not answer or names another issuer, since the sign-in would fail at the next start. |
| `Auth__Oidc__ClientId` | string | empty | yes | Client id registered at the provider. |
| `Auth__Oidc__ClientSecret` | string | empty | yes | Client secret (confidential client). |
| `Auth__Oidc__Scopes__0`, `Auth__Oidc__Scopes__1`, ... | list of scopes | `openid`, `profile`, `email` | no | Scopes requested. The default three are always requested; values given here are **added** to them (listing a default again is harmless), so only give extra scopes such as `groups`. The defaults can only be changed in code. |

Provider-side setup: register the redirect URI `https://<your-host>/auth/oidc/callback` (and, if the provider asks, the post-logout URI `https://<your-host>/auth/oidc/signed-out`). Claims used: `sub` (required, the stable identity), `email` (ignored when `email_verified` is `false`, so an unverified address cannot claim an administrator e-mail), `name` or `preferred_username` (display name). Users are created at their first sign-in; a disabled user is refused.

How the browser signs in: the app sends a visitor to the provider at once (the sign-in screen shows only a spinner and a *Continue to sign in* link in case the redirect did not happen) and brings them back to the page they opened (`/auth/oidc/login?returnUrl=...`; only same-site paths are accepted). **Sign out** ends the app's session only; the provider's session stays, so the screen after signing out waits for a click on *Sign in* (remembered per browser tab) instead of signing the user straight back in, and a fresh tab signs in silently while the provider session lasts. A sign-in that fails (cancelled at the provider, refused by it, a stale callback, a disabled account) lands on `<page>?signIn=failed&reason=<code>` with an explanation and a *Try again* button; the codes are `access_denied`, `provider_error`, `token_rejected`, `no_subject`, `account_disabled` and `callback_rejected`, and the app's own log line is one Warning, `OIDC sign-in failed: <code> (<exception type>)`, never the provider's text (a disabled account also keeps its refusal line, and the framework may add a line of its own about a token endpoint error). A provider that cannot be reached when the sign-in starts still answers `/auth/oidc/login` with a plain server error.

## Proxy header settings (`Auth__Mode=ProxyHeader`)

| Environment variable | Type | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `Auth__ProxyHeader__TrustedProxies__0`, `__1`, ... | list of IP addresses or CIDR ranges, e.g. `10.0.0.0/8`, `172.18.0.5`, `fd00::/8` | empty | **yes, at least one** | The proxies whose headers are trusted. A request from any other address has its user header ignored, otherwise anyone could forge it. |
| `Auth__ProxyHeader__UserHeader` | header name | `X-Forwarded-User` | yes | Header carrying the user name (a single, non-empty value without commas, otherwise the request is rejected). Also matched against `Auth__AdminEmails__n`. |
| `Auth__ProxyHeader__EmailHeader` | header name, or empty to ignore | `X-Forwarded-Email` | no | Header carrying the user's e-mail address. |

The proxy must also strip these headers from incoming client requests, set them itself, and connect from an address listed in `TrustedProxies`. Users are created at their first request; a disabled user is refused.

## E-mail settings (`Smtp__*`, used by Standalone to send password setup/reset links)

Without `Smtp__Host` and `Smtp__From` no e-mail is sent; administrators issue links by hand and hand them over. Setting both requires `Auth__PublicUrl`. A mail server that takes the connection but does not finish sending within 20 seconds counts as one that could not be reached.

| Environment variable | Type / values | Default | Required | Meaning |
| --- | --- | --- | --- | --- |
| `Smtp__Host` | host name | empty | to enable e-mail | SMTP server. |
| `Smtp__From` | e-mail address | empty | to enable e-mail | Sender address. |
| `Smtp__Port` | integer | `587` | no | SMTP port (`465` for implicit TLS, `25` for plain). |
| `Smtp__Security` | `Auto`, `None`, `StartTls`, `StartTlsWhenAvailable`, `SslOnConnect` | `Auto` | no | Transport security (MailKit's names). `Auto` picks from the port: TLS from the start on 465, otherwise STARTTLS when the server offers it. `Ssl`, which this table used to list, is not one of them and runs as `Auto`; use `SslOnConnect` for TLS from the start. |
| `Smtp__Username` | string | empty | no | Login for the SMTP server (authentication is skipped when empty). |
| `Smtp__Password` | string | empty | no | Password for the SMTP server. |

## Copy-and-adapt examples

**Standalone** (the first start opens the [setup page](#the-first-start-setup), which makes the administrator):

```sh
Auth__Mode=Standalone
Auth__PublicUrl=https://tankstat.example.com
Smtp__Host=smtp.example.com
Smtp__Port=587
Smtp__Username=tankstat@example.com
Smtp__Password=...
Smtp__From=tankstat@example.com
```

**Oidc:**

```sh
Auth__Mode=Oidc
Auth__Oidc__Authority=https://id.example.com
Auth__Oidc__ClientId=tankstat
Auth__Oidc__ClientSecret=...
Auth__AdminEmails__0=you@example.com
```

**ProxyHeader:**

```sh
Auth__Mode=ProxyHeader
Auth__ProxyHeader__TrustedProxies__0=10.0.0.0/8
Auth__ProxyHeader__UserHeader=X-Forwarded-User
Auth__ProxyHeader__EmailHeader=X-Forwarded-Email
Auth__AdminEmails__0=you@example.com
```

**None** (development only): `Auth__Mode=None`.

## Checklist for an automated setup

1. Set `Auth__Mode` explicitly (never rely on the shipped `appsettings.json`).
2. Set every setting marked *required* in the table of that mode; the app tells you at start which one is missing. So that no browser setup waits: in `Standalone` finish the setup with one request ([above](#the-first-start-setup)), in `Oidc` and `ProxyHeader` name the administrators (`Auth__AdminEmails__0`, ...), or turn the setup off (`Setup__Enabled=false`; with sign-in off that is the only way, and in `Standalone` only once an administrator exists).
3. Behind a TLS-terminating proxy keep `Auth__PublicUrl` and the OIDC redirect URI on the public `https://` address (the session cookie accepts plain HTTP behind the proxy).
4. For `Standalone` with e-mail, set `Auth__PublicUrl` together with `Smtp__Host` and `Smtp__From`.
5. Check the result: `{ session { mode user { email isAdmin } } }` at `/graphql` answers the active mode (and `notices` carries the `AUTH_DISABLED` warning when authentication is off, `SETTINGS_FILE_IGNORED` when the app's own settings file was left out at start, `RESTORE_PENDING` while a restore waits for the next start, or `SETUP_PENDING` while the instance waits for its setup).

## Access control

Every vehicle and refuelling belongs to a user. Access levels are ordered `None < View < Edit < Delete`:

- **Edit** creates, changes, moves to the trash and restores; **Delete** additionally deletes permanently (empties the trash). The same rule applies to every kind of data.
- Owners and administrators implicitly have Delete. An administrator decides what others may do with someone else's data: an instance-wide default (nothing, view, edit or delete) plus per-user grants ("Bob may edit Alice's data").
- **Sharing a vehicle's logs.** Anyone who can edit a vehicle (the owner, an administrator, or an editor) can give another user *Edit* or *Delete* access to **that vehicle's logs** only (generic `ResourceGrant`s, "Logs" feature). The grantee sees the vehicle and works with its logs, but cannot change the vehicle or anything else; nobody can give more access than they hold.

Everything goes through one access layer (`AccessService`), so new kinds of data only need to implement `IOwned`. Without authentication everyone has full access.
