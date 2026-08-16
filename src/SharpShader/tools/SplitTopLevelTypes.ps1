# Splits a C# file into one file per top-level type. Nested types stay with the parent.
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$sourcePath = (Resolve-Path $Path).Path
$outputDir = if ($OutputDirectory) { $OutputDirectory } else { Split-Path $sourcePath -Parent }
$lines = [System.IO.File]::ReadAllLines($sourcePath)
$text = [System.IO.File]::ReadAllText($sourcePath)

$header = New-Object System.Collections.Generic.List[string]
$i = 0
while ($i -lt $lines.Length) {
    $line = $lines[$i]
    if ($line -match '^\s*(namespace|public |internal |private |protected |\[|///|//|\s*$)') {
        if ($line -match '^\s*namespace\s+') { break }
        if ($line -match '^\s*(public|internal|private|protected)\s+(enum|class|struct|record|interface|static class|readonly struct|sealed class|readonly record struct)') { break }
        if ($line -match '^\s*\[') {
            # attribute might belong to a type; peek ahead
            $peek = $i
            while ($peek -lt $lines.Length -and $lines[$peek] -match '^\s*(\[|///|//)') { $peek++ }
            if ($peek -lt $lines.Length -and $lines[$peek] -match '^\s*(public|internal)\s+') { break }
        }
        if ($line -match '^\s*///') {
            $peek = $i
            while ($peek -lt $lines.Length -and $lines[$peek] -match '^\s*(///|//|\s*$)') { $peek++ }
            if ($peek -lt $lines.Length -and $lines[$peek] -match '^\s*(\[|(public|internal)\s+)') { break }
        }
    }
    $header.Add($line)
    $i++
}

function Get-TypeName([string]$decl) {
    if ($decl -match '\b(?:enum|class|struct|interface|record)\s+([A-Za-z_][A-Za-z0-9_]*)') {
        return $Matches[1]
    }
    return $null
}

$types = New-Object System.Collections.Generic.List[object]
$start = $i
$depth = 0
$started = $false
$currentStart = $i
$currentName = $null
$inType = $false

for (; $i -le $lines.Length; $i++) {
    $line = if ($i -lt $lines.Length) { $lines[$i] } else { "}" }
    $stripped = [regex]::Replace($line, '//.*$', '')
    $stripped = [regex]::Replace($stripped, '"([^"\\]|\\.)*"', '""')

    if (-not $inType) {
        if ($line -match '^\s*(public|internal)\s+' -or $line -match '^\s*\[|^\s*///') {
            $look = $i
            while ($look -lt $lines.Length -and $lines[$look] -match '^\s*(\[|///|//|\s*$)') { $look++ }
            if ($look -lt $lines.Length -and $lines[$look] -match '^\s*(public|internal)\s+') {
                $currentStart = $i
                $currentName = Get-TypeName $lines[$look]
                $inType = $true
                $depth = 0
                $started = $false
            }
        }
    }

    if ($inType) {
        foreach ($ch in $stripped.ToCharArray()) {
            if ($ch -eq '{') { $depth++; $started = $true }
            elseif ($ch -eq '}') { $depth-- }
        }
        if ($started -and $depth -le 0) {
            $end = [Math]::Min($i, $lines.Length - 1)
            $body = $lines[$currentStart..$end]
            $types.Add([pscustomobject]@{ Name = $currentName; Lines = $body })
            $inType = $false
        }
    }
}

if ($types.Count -lt 2) {
    Write-Host "Skip $sourcePath (types=$($types.Count))"
    return
}

$nsOpen = ($header -join "`n") -match 'namespace\s+[A-Za-z0-9_.]+\s*\{'
$headerText = ($header -join "`r`n").TrimEnd()
if (-not $headerText.EndsWith("{") -and $text -match '(?m)^namespace\s+[A-Za-z0-9_.]+\s*$') {
    $nsOpen = $false
}

foreach ($type in $types) {
    if (-not $type.Name) { throw "Unnamed type in $sourcePath" }
    $outPath = Join-Path $outputDir "$($type.Name).cs"
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.AppendLine($headerText)
    if ($headerText -notmatch 'namespace') {
        throw "No namespace header for $sourcePath"
    }
    [void]$sb.AppendLine()
    foreach ($line in $type.Lines) {
        [void]$sb.AppendLine($line)
    }
    if ($headerText -match 'namespace\s+[A-Za-z0-9_.]+\s*$') {
        # file-scoped or block without brace on same line handled by remaining lines
    }
    $content = $sb.ToString()
    if ($content -match '(?m)^namespace\s+[A-Za-z0-9_.]+\s*$' -and $content -notmatch '(?m)^namespace\s+[A-Za-z0-9_.]+\s*\{') {
        # namespace without brace: original used file-scoped? SharpShader uses block namespace
    }
    # Ensure closing namespace brace if header opened one
    if ($headerText -match 'namespace\s+.+\{\s*$' -or ($lines | Select-Object -Last 1) -eq '}') {
        if ($content.TrimEnd() -notmatch '\}\s*$') {
            $content += "}`r`n"
        }
        # If type body already includes final file brace only once
    }
    [System.IO.File]::WriteAllText($outPath, $content.TrimEnd() + "`r`n")
    Write-Host "Wrote $outPath"
}

Remove-Item $sourcePath
Write-Host "Removed $sourcePath"
