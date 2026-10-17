param([ValidateSet('Debug','Release')][string]$Configuration='Release',[switch]$Native)
$ErrorActionPreference='Stop'
$php=& (Join-Path $PSScriptRoot 'Setup-BackendRuntime.ps1')
foreach($file in @('Backend/private/app.php','Backend/public/index.php','Backend/router.php')){
 & $php -c (Join-Path (Split-Path $php) 'php.ini') -d ('extension_dir='+(Join-Path (Split-Path $php) 'ext')) -l (Join-Path $PSScriptRoot $file)
 if($LASTEXITCODE -ne 0){throw 'PHP syntax check failed'}
}
$arguments=@((Join-Path $PSScriptRoot "BackendChecks/bin/$Configuration/net10.0/BridgeCrew.BackendChecks.dll"),$PSScriptRoot,$php)
if($Native){$arguments+=Join-Path $PSScriptRoot "WinUI/bin/$Configuration/net10.0-windows10.0.19041.0/win-x64/BridgeCrew.Launcher.exe"}
& dotnet @arguments
if($LASTEXITCODE -ne 0){throw 'Backend integration checks failed'}
