$ErrorActionPreference='Stop'
$runtime=Join-Path $PSScriptRoot '.tools/php'
$exe=Join-Path $runtime 'php.exe'
if(!(Test-Path -LiteralPath $exe)){
 New-Item -ItemType Directory -Force $runtime|Out-Null
 $zip=Join-Path $PSScriptRoot '.tools/php-runtime.zip'
 try{Invoke-WebRequest 'https://windows.php.net/downloads/releases/php-8.4.26-nts-Win32-vs17-x64.zip' -OutFile $zip}
 catch{Invoke-WebRequest 'https://windows.php.net/downloads/releases/archives/php-8.4.26-nts-Win32-vs17-x64.zip' -OutFile $zip}
 if((Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant() -ne 'da68394f9193b7f6b89d0c76861a4034ae10efee7fd55a7255d8118c2acf70d7'){throw 'PHP runtime checksum mismatch'}
 Expand-Archive -LiteralPath $zip -DestinationPath $runtime -Force
}
@'
extension_dir=ext
extension=pdo_sqlite
extension=sqlite3
extension=openssl
memory_limit=128M
max_execution_time=30
display_errors=Off
log_errors=On
'@|Set-Content -LiteralPath (Join-Path $runtime 'php.ini')
Write-Output $exe
