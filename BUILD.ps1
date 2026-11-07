param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0',
    [string]$Destination = (Join-Path $PSScriptRoot 'dist')
)
$ErrorActionPreference = 'Stop'
$sourceRoot = $PSScriptRoot
$upstream = [IO.Path]::GetFullPath((Join-Path $sourceRoot '../Multiplayer'))
if (-not (Test-Path -LiteralPath $upstream)) {
    git clone --branch v0.11.5 --single-branch https://github.com/rwmt/Multiplayer.git $upstream
    if ($LASTEXITCODE -ne 0) { throw 'git clone failed' }
}
$head = git -C $upstream rev-parse HEAD
if ($head -ne '4a3be276bbf90cc597abfa5b299935ca8eeeb285') { throw 'Unexpected upstream commit' }
foreach ($patchName in @('server-fixes.patch', 'client-fixes.patch')) {
$patch = Join-Path $sourceRoot $patchName
git -C $upstream apply --check $patch 2>$null
if ($LASTEXITCODE -eq 0) {
    git -C $upstream apply $patch
    if ($LASTEXITCODE -ne 0) { throw 'Failed to apply server fixes' }
} else {
    git -C $upstream apply --reverse --check $patch
    if ($LASTEXITCODE -ne 0) { throw 'Upstream has unexpected local modifications' }
}
}
$cache = Join-Path $sourceRoot '.build'
New-Item -ItemType Directory -Path $cache -Force | Out-Null
$archive = Join-Path $cache 'Multiplayer-v0.11.5.zip'
$expectedHash = 'FB34AC6EFA79A64746AF8F53FAE9D8959796A26262951F653CDC38C2DA2196F5'
if (-not (Test-Path -LiteralPath $archive) -or (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) {
    Invoke-WebRequest 'https://github.com/rwmt/Multiplayer/releases/download/v0.11.5/Multiplayer-v0.11.5.zip' -OutFile $archive
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expectedHash) { throw 'Archive checksum mismatch' }
dotnet test (Join-Path $upstream 'Source/Tests/Tests.csproj') -c Release --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Upstream regression tests failed' }
dotnet build (Join-Path $upstream 'Source/Client/Multiplayer.csproj') -c Release
if ($LASTEXITCODE -ne 0) { throw 'Guard client build failed' }
$guardRoot = Join-Path $cache 'guard'
$guardFiles = [ordered]@{}
foreach ($pair in @(
    @('Source/Client/bin/Multiplayer.dll', '1.6/AssembliesCustom/Multiplayer.dll'),
    @('Source/Client/bin/MultiplayerCommon.dll', '1.6/AssembliesCustom/MultiplayerCommon.dll'),
    @('Source/Client/bin/MultiplayerLoader.dll', '1.6/Assemblies/MultiplayerLoader.dll')
)) {
    $target = Join-Path $guardRoot $pair[1]
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $upstream $pair[0]) -Destination $target -Force
    $guardFiles[$pair[1]] = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
}
@{ Revision='rt-guard1'; Protocol=5401; Files=$guardFiles } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $guardRoot 'manifest.json') -Encoding utf8NoBOM
$previousExport = $env:RIMTOGETHER_TEST_REPLAY
$previousGuardPayload = $env:RIMTOGETHER_GUARD_PAYLOAD
try {
    $env:RIMTOGETHER_TEST_REPLAY = Join-Path $cache 'synthetic-replay.zip'
    $env:RIMTOGETHER_GUARD_PAYLOAD = $guardRoot
    dotnet run --project (Join-Path $sourceRoot 'Tests/Tests.csproj') -c Release -- $archive
    if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
} finally { $env:RIMTOGETHER_TEST_REPLAY = $previousExport; $env:RIMTOGETHER_GUARD_PAYLOAD = $previousGuardPayload }
$destinationFull = [IO.Path]::GetFullPath($Destination)
foreach ($project in @('Launcher', 'Server', 'Bootstrap')) {
    $target = if ($project -eq 'Server') { Join-Path $destinationFull 'server' } else { $destinationFull }
    dotnet publish (Join-Path $sourceRoot "$project/$project.csproj") -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true "-p:Version=$Version" -o $target
    if ($LASTEXITCODE -ne 0) { throw "$project build failed" }
}
$previousIntegration = $env:RIMTOGETHER_INTEGRATION_DIR
try {
    $env:RIMTOGETHER_INTEGRATION_DIR = Join-Path $cache 'process-test'
    python (Join-Path $sourceRoot 'integration.py') (Join-Path $cache 'synthetic-replay.zip') (Join-Path $destinationFull 'server/RimTogether.Server.exe')
    if ($LASTEXITCODE -ne 0) { throw 'Standalone process integration tests failed' }
} finally { $env:RIMTOGETHER_INTEGRATION_DIR = $previousIntegration }
$packages = Join-Path $destinationFull 'packages'
New-Item -ItemType Directory -Path $packages -Force | Out-Null
Copy-Item -LiteralPath $archive -Destination $packages -Force
New-Item -ItemType Directory -Path (Join-Path $packages 'guard') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $guardRoot 'manifest.json') -Destination (Join-Path $packages 'guard') -Force
foreach ($relative in $guardFiles.Keys) {
    $target = Join-Path $packages "guard/$relative"
    New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $guardRoot $relative) -Destination $target -Force
}
Copy-Item -LiteralPath (Join-Path $upstream 'LICENSE') -Destination (Join-Path $destinationFull 'LICENSE-Multiplayer.txt')
Copy-Item -LiteralPath (Join-Path $sourceRoot 'START-HERE-RU.txt') -Destination $destinationFull -Force
$releaseFolder = Join-Path $cache 'release'
New-Item -ItemType Directory -Path $releaseFolder -Force | Out-Null
$bundle = Join-Path $releaseFolder 'RimTogether-win-x64.zip'
if (Test-Path -LiteralPath $bundle) { Remove-Item -LiteralPath $bundle }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($bundle, [IO.Compression.ZipArchiveMode]::Create)
try {
    $releaseFiles = @('RimTogether.exe', 'RimTogether.Start.exe', 'server/RimTogether.Server.exe', 'packages/Multiplayer-v0.11.5.zip', 'packages/guard/manifest.json', 'START-HERE-RU.txt', 'LICENSE-Multiplayer.txt') + @($guardFiles.Keys | ForEach-Object { "packages/guard/$_" })
    foreach ($relative in $releaseFiles) {
        $file = Join-Path $destinationFull $relative
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
$commit = git -C $sourceRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source must be committed before packaging' }
$manifest = [ordered]@{
    Schema = 1; Tag = "v$Version"; Commit = "$commit"; Archive = 'RimTogether-win-x64.zip';
    Sha256 = (Get-FileHash -LiteralPath $bundle -Algorithm SHA256).Hash;
    Size = (Get-Item -LiteralPath $bundle).Length; MultiplayerTag = 'v0.11.5'; CheckpointSchema = 1
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseFolder 'RimTogether-manifest.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $destinationFull 'RimTogether.Start.exe') -Destination $releaseFolder -Force
Write-Output "Release artifacts: $releaseFolder"
