param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$helper=$null
$output=Join-Path $PSScriptRoot 'artifacts/checks'
New-Item -ItemType Directory -Path $output -Force|Out-Null
try{
 $health=$null
 try{$health=Invoke-RestMethod 'http://127.0.0.1:38471/health' -TimeoutSec 1}catch{}
 if($null -eq $health){
  $server=Join-Path $PSScriptRoot "Directory/bin/$Configuration/net10.0/BridgeCrew.Directory.dll"
  $helper=Start-Process -FilePath 'dotnet.exe' -ArgumentList ('"'+$server+'"') -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $output 'directory.log') -RedirectStandardError (Join-Path $output 'directory-error.log')
  for($attempt=0;$attempt -lt 30;$attempt++){try{$health=Invoke-RestMethod 'http://127.0.0.1:38471/health' -TimeoutSec 1;break}catch{Start-Sleep -Milliseconds 200}}
 }
 if($health.mode -ne 'local-preview' -or $health.protocol -ne 1){throw 'Unexpected service on the preview directory port.'}
 dotnet (Join-Path $PSScriptRoot "Checks/bin/$Configuration/net10.0/BridgeCrew.Checks.dll") (Join-Path $output 'state') --http | Tee-Object -FilePath (Join-Path $output 'results.txt')
 if($LASTEXITCODE -ne 0){throw 'Pack checks failed.'}
}finally{if($null -ne $helper -and !$helper.HasExited){Stop-Process -Id $helper.Id}}
& (Join-Path $PSScriptRoot 'Test-Backend.ps1') -Configuration $Configuration
