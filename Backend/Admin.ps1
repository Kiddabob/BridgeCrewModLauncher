param([Parameter(Mandatory)][ValidateSet('publisher','hide-pack','account','reports')][string]$Action,[string]$Username,[string]$PackId,[string]$Version,[switch]$Disable)
$ErrorActionPreference='Stop'
$owner=Get-Content -LiteralPath (Join-Path $PSScriptRoot '../.state/hostinger-owner.json') -Raw|ConvertFrom-Json
if($Action -eq 'reports'){Invoke-RestMethod -Uri ($owner.api+'admin/reports') -MaximumRedirection 0 -Headers @{'X-BridgeCrew-Admin'=$owner.adminKey};return}
if($Action -in 'publisher','account'){
 if([string]::IsNullOrWhiteSpace($Username)){throw 'Supply -Username'}
 $body=if($Action -eq 'publisher'){@{username=$Username;enabled=!$Disable}}else{@{username=$Username;disabled=[bool]$Disable}}
}else{
 if([string]::IsNullOrWhiteSpace($PackId)-or [string]::IsNullOrWhiteSpace($Version)){throw 'Supply -PackId and -Version'}
 $body=@{id=$PackId;version=$Version;hidden=!$Disable}
}
Invoke-RestMethod -Uri ($owner.api+'admin/'+$Action) -Method Post -ContentType 'application/json' -Body ($body|ConvertTo-Json) -MaximumRedirection 0 -Headers @{'X-BridgeCrew-Admin'=$owner.adminKey}
