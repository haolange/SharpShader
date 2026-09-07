#requires -Version 7.4
[CmdletBinding()]
param(
    [string] $OutputRoot = (Split-Path -Parent $PSScriptRoot),
    [string[]] $TargetRids = @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64'),
    [string] $ExternalAssetRoot
)

$ErrorActionPreference = 'Stop'
$productRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -LiteralPath (Join-Path $productRoot 'native/assets.json') -Raw | ConvertFrom-Json
$outputDirectory = [IO.Path]::GetFullPath($OutputRoot)
$cacheDirectory = Join-Path $outputDirectory 'artifacts/downloads'
[IO.Directory]::CreateDirectory($cacheDirectory) | Out-Null

function Get-ContainedPath([string] $Root, [string] $Relative)
{
    $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (!$resolved.StartsWith($prefix, $comparison)) { throw "Asset path escaped its root: $Relative" }
    return $resolved
}

function Assert-Hash([string] $Path, [string] $Expected)
{
    if ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Expected)
    {
        throw "SHA256 mismatch: $Path. Existing files are never silently replaced."
    }
}

function Read-ArchiveMember([string] $Archive, [string] $Format, [string] $Member, [string] $Destination)
{
    $output = [IO.File]::Create($Destination)
    try
    {
        if ($Format -eq 'zip')
        {
            $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
            try
            {
                $entry = $zip.GetEntry($Member)
                if ($null -eq $entry) { throw "Missing archive member: $Member" }
                $input = $entry.Open()
                try { $input.CopyTo($output) } finally { $input.Dispose() }
            }
            finally { $zip.Dispose() }
        }
        elseif ($Format -eq 'tar.gz')
        {
            $file = [IO.File]::OpenRead($Archive)
            $gzip = [IO.Compression.GZipStream]::new($file, [IO.Compression.CompressionMode]::Decompress)
            $tar = [System.Formats.Tar.TarReader]::new($gzip)
            try
            {
                $found = $false
                while ($null -ne ($entry = $tar.GetNextEntry()))
                {
                    if ($entry.Name -eq $Member)
                    {
                        if ($null -eq $entry.DataStream) { throw "Archive member is not a regular data file: $Member" }
                        $entry.DataStream.CopyTo($output)
                        $found = $true
                        break
                    }
                }
                if (!$found) { throw "Missing archive member: $Member" }
            }
            finally { $tar.Dispose(); $gzip.Dispose(); $file.Dispose() }
        }
        else { throw "Unsupported archive format: $Format" }
    }
    finally { $output.Dispose() }
}

$lockPath = Join-Path $cacheDirectory 'restore.lock'
$restoreLock = [IO.File]::Open($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try
{
    $assets = @($manifest.assets | Where-Object rid -In $TargetRids)
    foreach ($rid in $TargetRids)
    {
        if ($rid -notin $manifest.assets.rid) { throw "No native assets are declared for RID $rid." }
    }
    foreach ($asset in $assets)
    {
        $destination = Get-ContainedPath $outputDirectory $asset.path
        if (Test-Path -LiteralPath $destination)
        {
            Assert-Hash $destination $asset.sha256
            Write-Output "Verified $($asset.path)"
            continue
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
        try
        {
            if ($asset.source.kind -eq 'archive')
            {
                $archive = Get-ContainedPath $cacheDirectory ($asset.source.archiveSha256 + '.download')
                if (!(Test-Path -LiteralPath $archive))
                {
                    $download = $archive + '.tmp'
                    try
                    {
                        Invoke-WebRequest -Uri $asset.source.archiveUrl -OutFile $download
                        Assert-Hash $download $asset.source.archiveSha256
                        [IO.File]::Move($download, $archive)
                    }
                    finally { if (Test-Path -LiteralPath $download) { Remove-Item -LiteralPath $download } }
                }
                Assert-Hash $archive $asset.source.archiveSha256
                Read-ArchiveMember $archive $asset.source.format $asset.source.member $temporary
            }
            elseif ($ExternalAssetRoot)
            {
                $external = Get-ContainedPath $ExternalAssetRoot $asset.path
                Assert-Hash $external $asset.sha256
                [IO.File]::Copy($external, $temporary)
            }
            else
            {
                throw "Unresolved upstream source for $($asset.path). Supply -ExternalAssetRoot with the recorded payload; public redistribution remains unverified."
            }
            Assert-Hash $temporary $asset.sha256
            if ((Get-Item -LiteralPath $temporary).Length -ne $asset.bytes) { throw "Asset size mismatch: $($asset.path)" }
            [IO.File]::Move($temporary, $destination)
            Write-Output "Restored $($asset.path)"
        }
        finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
    }
}
finally { $restoreLock.Dispose() }
