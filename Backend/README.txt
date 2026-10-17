Crew service
============

PHP 8.3+ with PDO SQLite and Argon2id password support. No daemon, external
database account, Composer dependencies, or scheduled task is required.

Hosted API: https://kiddabob.co.uk/BridgeCrew/api/v1/
Public files: /domains/kiddabob.co.uk/public_html/BridgeCrew
Private code/config/database/content: /domains/kiddabob.co.uk/bridgecrew-launcher

The BridgeCrew directory alone selects PHP 8.4 via its own .htaccess. Existing
sites keep their PHP selection, files, database configuration and scheduled jobs.
The private SQLite store and uploaded payloads are outside public_html.

Owner operations (run from the launcher checkout)
------------------------------------------------
  ./Backend/Admin.ps1 -Action publisher -Username YOUR_ACCOUNT
  ./Backend/Admin.ps1 -Action publisher -Username ACCOUNT -Disable
  ./Backend/Admin.ps1 -Action hide-pack -PackId example -Version 1.0.0
  ./Backend/Admin.ps1 -Action hide-pack -PackId example -Version 1.0.0 -Disable
  ./Backend/Admin.ps1 -Action account -Username ACCOUNT -Disable
  ./Backend/Admin.ps1 -Action account -Username ACCOUNT
  ./Backend/Admin.ps1 -Action reports

Accounts start without publishing permission. This prevents open anonymous
file hosting while allowing players to sign up, subscribe and join rooms.
Owner credentials are in .state/hostinger-owner.json, excluded from Git and
installer packages. Do not share that file. Keep a private backup of it and
the private hosting data directory. The launcher never receives the admin key.

Deployment
----------
  python Backend/deploy.py

Uses the saved FileZilla Kiddabob.co.uk account over verified FTPS. Only the
explicit BridgeCrew files are written. Deployments compare their previous
hashes, reject unknown drift and verify existing-site baseline hashes. The
database, blobs, other sites, domain-wide PHP settings and cron jobs are not
replaced. First deployment used a separately recorded read-only site baseline.

Limits and state
----------------
Current caps: 64 MiB per blob, 256 MiB owned payloads per publisher, 2 GiB total
payload storage, 100 releases per publisher, 1000 registered accounts. Request,
sign-in, registration, uploads and publishing have separate rate limits.
Metadata requests are at most 1 MiB; payloads stream to private temporary files.
Published versions cannot be edited or replaced. Hidden/disabled publishers'
releases cannot be retrieved through the API. Already-downloaded files remain
on subscribers' computers. The service does not scan or qualify mod behavior.

Sessions expire after seven days. Password recovery rotates a 256-bit recovery
secret and revokes previous sessions. Passwords use Argon2id; bearer tokens and
recovery secrets are stored as SHA-256 hashes. All remote requests require HTTPS.
The native app binds encrypted saved sessions and subscription caches to the
chosen service, and refuses HTTP redirects on API calls.

Room leases last 120 seconds; active clients heartbeat every 30 seconds. Only
the host renews the room itself. Friends/private visibility is checked by the
server, as are capacity, blocks, invitation secrets, pack locks and revisions.
Readiness is a launcher claim bound to its reviewed pack; it proves no gameplay.

Pack folder layout
------------------
  manifest.json         Existing PackManifest schema (see Core/Models.cs)
  content/<sha256>      Raw payload files named by their lowercase SHA-256

The launcher's Publish a pack folder uploads all edition variants, verifies
their declared sizes/hashes and publishes the immutable manifest. A mod SDK,
authoring UI, game-file activation and game joining are later work.

Testing
-------
  ./Build.ps1
  ./Test.ps1                   Pack checks plus local authenticated HTTP checks
  ./Test-Backend.ps1 -Native   Adds real WinUI sign-in/room/encryption checks

Setup-BackendRuntime.ps1 installs a checksum-pinned PHP runtime under .tools;
it makes no system-wide PHP installation or PATH change. Local tests create
private disposable accounts/databases and never use the production owner key.
HostedSmoke can verify an explicitly selected deployment; it hides its fixture
pack and disables its synthetic accounts afterward. Test JSON is in artifacts.
