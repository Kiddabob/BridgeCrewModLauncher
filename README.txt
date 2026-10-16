Bridge Crew Mod Launcher
========================

A native WinUI 3 Windows launcher for versioned Star Trek: Bridge Crew mod packs.
Meta PC and Steam are separate targets in the shared pack format from the start.

Download and install
--------------------
https://github.com/Kiddabob/BridgeCrewModLauncher/releases/latest

Use Kidda.BridgeCrewModLauncher-win-Setup.exe from a release. The per-user
installer creates Start Menu and Desktop shortcuts and an uninstall entry.
Windows x64; the app includes its .NET and Windows App SDK runtime dependencies.
The installer checks the native Visual C++ runtime and installs it if missing.
Settings -> App updates checks published GitHub releases and can download and
apply an update with a launcher restart. Pack subscriptions live separately in
%LOCALAPPDATA%\Kidda\BridgeCrewModLauncher and survive app updates.

Current release: early launcher preview
---------------------------------------
Implemented: native Windows controls, Windows/light/dark theme selection,
responsive layout, sample lobby search, per-mod subscription review, Meta/Steam
payload resolution, pinned pack identity, verified cache, manifest export,
subscription library, installer and GitHub-backed launcher updates.

Sample lobbies are examples, not real players. Their payloads are harmless JSON
fixtures, not working game mods. Game connection, accounts, public pack hosting,
the in-game menu and automatic game restart/rejoin are not connected yet.
This release does not change or launch Bridge Crew. Cross-store gameplay has
not been qualified. The separate mod SDK/tool launcher is deferred until the
individual mod types have been tested.

Build from source
-----------------
Windows 11 x64 and .NET SDK 10.0. The pinned NuGet dependencies are restored
from nuget.org. No game files or extracted proprietary assets are required.

  ./Build.ps1
  ./Test.ps1
  ./Start-Preview.ps1

Create an installer:
  ./Package.ps1 -Version 0.2.4

Build.ps1 uses WinUI, not the earlier WPF prototype. Release assets contain the
launcher only; no game assemblies, DLC, game assets or inspection dumps.

Project layout
--------------
WinUI/      Native Windows UI, persistent settings and updater
Core/       Pack format, dependency/variant resolver and content cache
Directory/  Loopback-only sample HTTP directory for development/testing
Checks/     Executable pack-integrity and subscription tests
docs/       Architecture and development milestones

The production directory will require authenticated host presence, publisher
identity and immutable pack releases. See docs/architecture.txt.

Development releases
--------------------
GitHub Actions checks builds and the pack tests. Tags such as v0.2.0 build and
publish a self-contained Velopack release using the repository's GITHUB_TOKEN.
The application does not contain a GitHub token or require one for public updates.

Unofficial project. Not affiliated with Ubisoft, Meta, Valve or the Star Trek
rights holders. The game must be obtained separately through its normal store.
