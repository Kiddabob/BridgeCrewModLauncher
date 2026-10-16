param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$env:DOTNET_CLI_HOME=Join-Path $PSScriptRoot '.tool-state'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$tool=Join-Path $PSScriptRoot '.tools/vpk.exe'
if(!(Test-Path -LiteralPath $tool)){
 dotnet tool install vpk --version 1.2.0 --tool-path (Join-Path $PSScriptRoot '.tools') --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
 if($LASTEXITCODE -ne 0){throw 'Unable to install packaging tool.'}
}
$publish=Join-Path $PSScriptRoot "publish/$Version"
if([string]::IsNullOrWhiteSpace($OutputDirectory)){$OutputDirectory=Join-Path $PSScriptRoot "releases/$Version"}
dotnet publish (Join-Path $PSScriptRoot 'WinUI/BridgeCrew.Launcher.csproj') -c Release -r win-x64 --self-contained true -p:Version=$Version -p:WindowsAppSDKSelfContained=true --configfile (Join-Path $PSScriptRoot 'NuGet.Config') -o $publish --nologo
if($LASTEXITCODE -ne 0){throw 'Publish failed.'}
foreach($required in @('BridgeCrew.Launcher.exe','App.xbf','MainWindow.xbf','BridgeCrew.Launcher.pri','Assets/AppIcon.ico','Assets/AppIcon.png')){
 if(!(Test-Path -LiteralPath (Join-Path $publish $required))){throw "Missing published resource: $required"}
}
& $tool pack --packId Kidda.BridgeCrewModLauncher --packVersion $Version --packDir $publish --mainExe BridgeCrew.Launcher.exe --packTitle 'Bridge Crew Mod Launcher' --packAuthors Kiddabob --icon (Join-Path $PSScriptRoot 'WinUI/Assets/AppIcon.ico') --runtime win-x64 --framework vcredist143-x64 --shortcuts 'Desktop,StartMenuRoot' --outputDir $OutputDirectory
if($LASTEXITCODE -ne 0){throw 'Installer packaging failed.'}
Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object{$_.Extension -in '.exe','.nupkg','.zip','.json'} | ForEach-Object{
 $hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
 '{0}  {1}' -f $hash.Hash.ToLowerInvariant(),$_.Name
}|Set-Content -LiteralPath (Join-Path $OutputDirectory 'SHA256SUMS.txt')
