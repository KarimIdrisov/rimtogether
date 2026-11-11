param(
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '1.1.0',
    [string]$Destination = (Join-Path $PSScriptRoot 'dist'),
    [switch]$SkipTests
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
foreach ($patchName in @('server-fixes.patch', 'client-fixes.patch', 'protocol-tests.patch')) {
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
if (-not $SkipTests) {
    dotnet test (Join-Path $upstream 'Source/Tests/Tests.csproj') -c Release --nologo -v minimal --logger 'console;verbosity=normal' --logger 'trx;LogFileName=upstream.trx' --results-directory (Join-Path $cache 'test-results')
    if ($LASTEXITCODE -ne 0) { throw 'Upstream regression tests failed' }
}
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
@{ Revision='rt-guard3'; Protocol=5404; Files=$guardFiles } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $guardRoot 'manifest.json') -Encoding utf8NoBOM
$previousExport = $env:RIMTOGETHER_TEST_REPLAY
$previousGuardPayload = $env:RIMTOGETHER_GUARD_PAYLOAD
try {
    $env:RIMTOGETHER_TEST_REPLAY = Join-Path $cache 'synthetic-replay.zip'
    $env:RIMTOGETHER_GUARD_PAYLOAD = $guardRoot
    if (-not $SkipTests) {
        dotnet run --project (Join-Path $sourceRoot 'Tests/Tests.csproj') -c Release -- $archive
        if ($LASTEXITCODE -ne 0) { throw 'Regression tests failed' }
    }
} finally { $env:RIMTOGETHER_TEST_REPLAY = $previousExport; $env:RIMTOGETHER_GUARD_PAYLOAD = $previousGuardPayload }
$destinationFull = [IO.Path]::GetFullPath($Destination)
# Shared runtime files stay byte-identical between application releases.
$publishRoot = Join-Path $cache ('publish-' + [Guid]::NewGuid().ToString('N'))
foreach ($project in @('Server', 'Launcher')) {
    $target = Join-Path $publishRoot $project
    dotnet publish (Join-Path $sourceRoot "$project/$project.csproj") -c Release -m:1 -r win-x64 --self-contained true -p:PublishSingleFile=false "-p:Version=$Version" -o $target
    if ($LASTEXITCODE -ne 0) { throw "$project build failed" }
    $mergeRoot = if ($project -eq 'Server') { Join-Path $destinationFull 'server' } else { $destinationFull }
    foreach ($file in Get-ChildItem -LiteralPath $target -File -Recurse) {
        if ($file.Extension -in @('.pdb', '.xml')) { continue }
        if ($file.Name -eq 'Assembly-CSharp.dll' -or $file.Name -like 'UnityEngine*.dll') { throw 'Game reference assemblies must never enter a release' }
        $relative = [IO.Path]::GetRelativePath($target, $file.FullName)
        $destinationFile = Join-Path $mergeRoot $relative
        if ((Test-Path -LiteralPath $destinationFile) -and (Get-FileHash -LiteralPath $destinationFile).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) { throw "Shared publish collision: $relative" }
        New-Item -ItemType Directory -Path (Split-Path -Parent $destinationFile) -Force | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destinationFile -Force
    }
}
# The portable GUI entry point carries its own runtime. Its engine version is pinned
# so unrelated app releases don't force a replacement of this larger bootstrap.
$bootstrapTarget = Join-Path $publishRoot 'Bootstrap'
dotnet publish (Join-Path $sourceRoot 'Bootstrap/Bootstrap.csproj') -c Release -m:1 -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:Version=2.0.0 -o $bootstrapTarget
if ($LASTEXITCODE -ne 0) { throw 'GUI bootstrap build failed' }
Copy-Item -LiteralPath (Join-Path $bootstrapTarget 'RimTogether.Start.exe') -Destination $destinationFull -Force
$previousIntegration = $env:RIMTOGETHER_INTEGRATION_DIR
try {
    $env:RIMTOGETHER_INTEGRATION_DIR = Join-Path $cache 'process-test-transport2'
    if (-not $SkipTests) {
        python (Join-Path $sourceRoot 'integration.py') (Join-Path $cache 'synthetic-replay.zip') (Join-Path $destinationFull 'server/RimTogether.Server.exe')
        if ($LASTEXITCODE -ne 0) { throw 'Standalone process integration tests failed' }
    }
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
    $releaseFiles = @(Get-ChildItem -LiteralPath $destinationFull -File -Recurse | ForEach-Object { [IO.Path]::GetRelativePath($destinationFull, $_.FullName).Replace('\', '/') } | Sort-Object)
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
$filesIndex = [ordered]@{ Schema=1; Tag="v$Version"; Commit="$commit"; ArchiveSha256=$manifest.Sha256; Files=@() }
$assetNames = [Collections.Generic.HashSet[string]]::new()
foreach ($relative in $releaseFiles) {
    $sourceFile = Join-Path $destinationFull $relative
    $hash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256).Hash
    $assetName = 'part-' + $hash.ToLowerInvariant() + '.gz'
    $assetPath = Join-Path $releaseFolder $assetName
    if ($assetNames.Add($assetName)) {
        $inputStream = [IO.File]::OpenRead($sourceFile)
        $outputStream = [IO.File]::Create($assetPath)
        try {
            $gzipStream = [IO.Compression.GZipStream]::new($outputStream, [IO.Compression.CompressionLevel]::Optimal, $true)
            try { $inputStream.CopyTo($gzipStream) } finally { $gzipStream.Dispose() }
        } finally { $inputStream.Dispose(); $outputStream.Dispose() }
    }
    $filesIndex.Files += [ordered]@{ Path=$relative; Sha256=$hash; Size=(Get-Item -LiteralPath $sourceFile).Length; DownloadSize=(Get-Item -LiteralPath $assetPath).Length }
}
if ($assetNames.Count -gt 950) { throw 'Too many component assets for a release' }
$indexPath = Join-Path $releaseFolder 'RimTogether-files.json'
$filesIndex | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $indexPath -Encoding utf8NoBOM
$manifest.FilesIndexHash = (Get-FileHash -LiteralPath $indexPath -Algorithm SHA256).Hash
$manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $releaseFolder 'RimTogether-manifest.json') -Encoding utf8NoBOM
Copy-Item -LiteralPath (Join-Path $destinationFull 'RimTogether.Start.exe') -Destination $releaseFolder -Force
Write-Output "Release artifacts: $releaseFolder ($($releaseFiles.Count) files; $($assetNames.Count) compressed components)"
