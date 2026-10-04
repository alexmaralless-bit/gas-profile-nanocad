$ErrorActionPreference = 'Stop'

$env:DOTNET_CLI_HOME = Join-Path $PSScriptRoot '.dotnet_home'
$env:LOCALAPPDATA = Join-Path $PSScriptRoot '.appdata\local'
$env:APPDATA = Join-Path $PSScriptRoot '.appdata\roaming'
$env:NUGET_PACKAGES = Join-Path $PSScriptRoot '.nuget\packages'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_NOLOGO = '1'
New-Item -ItemType Directory -Force $env:DOTNET_CLI_HOME | Out-Null
New-Item -ItemType Directory -Force $env:LOCALAPPDATA | Out-Null
New-Item -ItemType Directory -Force $env:APPDATA | Out-Null
New-Item -ItemType Directory -Force $env:NUGET_PACKAGES | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$outputRoot = Join-Path $PSScriptRoot "artifacts\GasProfile.NanoCad\$stamp"
New-Item -ItemType Directory -Force $outputRoot | Out-Null

& dotnet build .\src\GasProfile.NanoCad\GasProfile.NanoCad.csproj --configuration Debug --no-restore -p:OutputPath="$outputRoot\" -p:AppendTargetFrameworkToOutputPath=false
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$latestFile = Join-Path $PSScriptRoot "artifacts\GasProfile.NanoCad\latest.txt"
Set-Content -Path $latestFile -Value $outputRoot -Encoding UTF8
Write-Host "Plugin output: $outputRoot"
Write-Host "NETLOAD: $(Join-Path $outputRoot 'GasProfile.NanoCad.dll')"
