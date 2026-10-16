$ErrorActionPreference='Stop'
$exe=Join-Path $PSScriptRoot 'WinUI/bin/Release/net10.0-windows10.0.19041.0/win-x64/BridgeCrew.Launcher.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Run Build.ps1 first.'}
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)