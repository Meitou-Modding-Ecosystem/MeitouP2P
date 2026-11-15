param(
    [Parameter(Mandatory)][string]$MeitouDir,
    [string]$Dotnet = 'dotnet',
    [string]$CoopDir,
    [string]$MaestroCore
)
$ErrorActionPreference = 'Stop'
$MeitouDir = (Resolve-Path -LiteralPath $MeitouDir).Path
$game = Get-Item -LiteralPath (Join-Path $MeitouDir 'MeitouClient.dll')
if ($game.VersionInfo.ProductVersion -notmatch '^0\.20\.0(\+|$)') { throw "Expected the Meitou 0.20.0 release, found $($game.VersionInfo.ProductVersion)." }
$opts = @('-c','Release','--nologo','--disable-build-servers','-p:NuGetAudit=false',"-p:MeitouDir=$MeitouDir")
function Build-Project([string]$Project, [string[]]$Extra = @()) {
    & $Dotnet build $Project @opts @Extra
    if ($LASTEXITCODE) { throw "Build failed for $Project" }
}
function Pack-Mod([string]$Source, [string]$Name, [string]$Assembly) {
    $out = Join-Path $PSScriptRoot "artifacts/mods/$Name"
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    Copy-Item -Path (Join-Path $Source 'pack/*') -Destination $out
    Copy-Item -LiteralPath (Join-Path $Source "src/bin/Release/net11.0/$Assembly.dll") -Destination $out
    Write-Host "Packaged $out"
}
Build-Project (Join-Path $PSScriptRoot 'src/MeitouP2P.csproj')
Pack-Mod $PSScriptRoot 'MeitouP2P' 'MeitouP2P'
if ($CoopDir) {
    $CoopDir = (Resolve-Path -LiteralPath $CoopDir).Path
    Build-Project (Join-Path $CoopDir 'src/MeitouP2P.Coop.csproj') @("-p:ConnectorProject=$PSScriptRoot/src/MeitouP2P.csproj")
    Pack-Mod $CoopDir 'MeitouP2P-Coop' 'MeitouP2P.Coop'
}
$extra = @()
if ($MaestroCore) { $extra += "-p:MaestroCore=$((Resolve-Path -LiteralPath $MaestroCore).Path)" }
Build-Project (Join-Path $PSScriptRoot 'tools/Probe/Probe.csproj') $extra
