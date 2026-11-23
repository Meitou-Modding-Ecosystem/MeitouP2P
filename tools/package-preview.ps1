param([Parameter(Mandatory)][string]$MeitouDir, [Parameter(Mandatory)][string]$CoopDir, [string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$CoopDir = (Resolve-Path -LiteralPath $CoopDir).Path
& "$root\build.ps1" -MeitouDir $MeitouDir -CoopDir $CoopDir -Dotnet $Dotnet
$stage = Join-Path $root ('artifacts\remote-preview-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path "$stage\mods", "$stage\licenses" | Out-Null
Copy-Item -Path "$CoopDir\tools\playtest\*" -Destination $stage
foreach ($name in 'MeitouP2P','MeitouP2P-Coop') { Copy-Item -LiteralPath "$root\artifacts\mods\$name" -Destination "$stage\mods" -Recurse }
& $Dotnet "$PSScriptRoot\Probe\bin\Release\net11.0\Probe.dll" stoat-start "$stage\mods\Stoat Walk"
if ($LASTEXITCODE) { throw 'Fixture generation failed.' }
$p2pCommit = git -C $root rev-parse HEAD
$coopCommit = git -C $CoopDir rev-parse HEAD
$dirty = [bool]((git -C $root status --porcelain) -or (git -C $CoopDir status --porcelain))
$game = (Get-Item -LiteralPath (Join-Path $MeitouDir 'MeitouClient.dll')).VersionInfo.ProductVersion
@{schemaVersion=1; gameBuild=$game; connectorCommit=$p2pCommit; coopCommit=$coopCommit; uncommittedSources=$dirty; remoteResult='pending volunteer trial'} |
    ConvertTo-Json | Set-Content -LiteralPath "$stage\preview.json" -Encoding UTF8
@"
Corresponding source for this preview (GPL-3.0-or-later code and tools):
https://github.com/Meitou-Modding-Ecosystem/MeitouP2P/tree/$p2pCommit
https://github.com/Meitou-Modding-Ecosystem/MeitouP2P-Coop/tree/$coopCommit

The README at each exact commit includes build instructions. Building requires
the .NET 11 SDK and your own Meitou/Kenshi installations. Players use Setup.ps1
and Launch.ps1; no SDK or Python is needed. See licenses/ for the licensing split.
"@ | Set-Content -LiteralPath "$stage\SOURCE.txt" -Encoding UTF8
Copy-Item -LiteralPath "$CoopDir\docs\remote-playtest.md" -Destination "$stage\READ-ME.md"
foreach ($source in @(@{folder=$root; name='MeitouP2P'}, @{folder=$CoopDir; name='MeitouP2P-Coop'})) {
    $destination = New-Item -ItemType Directory -Path (Join-Path "$stage\licenses" $source.name)
    Get-ChildItem -LiteralPath $source.folder -File | Where-Object { $_.Name -match '^(LICENSE|LICENSING|NOTICE)' } | Copy-Item -Destination $destination
    if (Test-Path -LiteralPath (Join-Path $source.folder 'LICENSES')) { Copy-Item -LiteralPath (Join-Path $source.folder 'LICENSES') -Destination $destination -Recurse }
}
$hashes = Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
    [pscustomobject]@{path=$_.FullName.Substring($stage.Length + 1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
}
$hashes | ConvertTo-Json | Set-Content -LiteralPath "$stage\checksums.json" -Encoding UTF8
Compress-Archive -Path "$stage\*" -DestinationPath "$stage.zip"
Write-Output "Preview folder: $stage"
Write-Output "Preview ZIP: $stage.zip"
