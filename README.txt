Bridge Crew Mod Launcher
========================

A native WinUI 3 Windows launcher for Star Trek: Bridge Crew mod packs, with
Meta PC and Steam variants supported by one pinned pack format.

Download and install
--------------------
https://github.com/Kiddabob/BridgeCrewModLauncher/releases/latest

Use Kidda.BridgeCrewModLauncher-win-Setup.exe. The per-user installer creates
Start Menu/Desktop shortcuts and an uninstall entry. Windows x64; .NET and
Windows App SDK runtimes are included. The installer handles the native C++
runtime if missing. Settings -> App updates checks and applies GitHub releases.

Online crew service (0.3.0)
---------------------------
Open Online, create an account and save the recovery code. Add friends, review
published packs, host a launcher room or find a friend’s room and subscribe.
Room hosts can select public/friends/invitation visibility, share invitations
and remove guests. Players can mark the verified launcher pack ready. Rooms
expire when their host stops renewing presence. Sign-in is remembered using
Windows user encryption; no store or Ubisoft passwords are requested.

The default HTTPS API is https://kiddabob.co.uk/BridgeCrew/api/v1/. It is hosted
in isolated folders on existing hosting, with a private SQLite database and
payload store. There is no new hosting subscription or domain-wide change.
Publishing requires service-owner approval; players can register and subscribe
without it. See Backend/README.txt for owner commands, quotas and deployment.
A pack folder contains manifest.json plus content/<sha256> payload files.

Sample lobbies are still available from Online -> Try sample lobbies. They
contain harmless JSON fixtures and can be used without creating an account.

Current boundary
----------------
A launcher room is working backend membership, not entry into Bridge Crew.
The game plugin, in-game menu, automatic game-lobby advertisement, game-file
activation and restart/rejoin are not implemented. No game files are included,
changed or launched by this release. Game compatibility and cross-store
play have not been qualified. SDK authoring tools remain deferred until the
individual mod types have been tested.

Settings, samples and existing subscriptions remain in
%LOCALAPPDATA%\Kidda\BridgeCrewModLauncher. Online subscriptions use a separate
cache per service so consent from another service does not carry across.

Build and test
--------------
Windows 11 x64, .NET SDK 10.0 and the pinned NuGet dependencies from nuget.org.
No game files or extracted proprietary assets are required.

  ./Build.ps1
  ./Test.ps1
  ./Test-Backend.ps1 -Native
  ./Start-Preview.ps1
  ./Package.ps1 -Version 0.3.0

The PHP integration runtime is checksum-pinned under .tools, with no global
installation or PATH change. The local API tests own their temporary accounts,
database, server process and content. Backend/private/config.php, .state,
artifacts, generated runtime files and production secrets are excluded from Git.

Project layout
--------------
WinUI/         Native Windows UI, encrypted sessions, rooms and updater
Core/          Pack rules, verified cache and authenticated service client
Backend/       PHP/SQLite HTTPS API and isolated Hostinger deployment tools
BackendChecks/ Real HTTP accounts/publishing/room and optional native UI checks
Directory/     Loopback-only sample fixture service
Checks/        Pack integrity, consent and subscription tests

API contract: docs/service-api.txt. Milestones: docs/architecture.txt.

GitHub Actions builds the launcher and runs pack/backend checks on main and
pull requests. Version tags build and publish self-contained Velopack releases.
The application has no embedded GitHub token, FTP credential or owner API key.

Unofficial project, not affiliated with Ubisoft, Meta, Valve or Star Trek’s
rights holders. Obtain the game separately through its normal store.
