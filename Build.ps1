param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference = 'Stop'
$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.tool-state'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
foreach ($project in @('Core/BridgeCrew.Packs.csproj','Directory/BridgeCrew.Directory.csproj','WinUI/BridgeCrew.Launcher.csproj','Checks/BridgeCrew.Checks.csproj')) {
    dotnet build (Join-Path $PSScriptRoot $project) -c $Configuration --configfile (Join-Path $PSScriptRoot 'NuGet.Config') --nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $project" }
}
