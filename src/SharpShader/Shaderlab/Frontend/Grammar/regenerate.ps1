# Regenerates ShaderLab ANTLR C# sources with the pinned 4.13.1 tool.
# Requires a Java runtime on PATH.
$ErrorActionPreference = "Stop"

$antlrVersion = "4.13.1"
$grammarDir = $PSScriptRoot
$generatedDir = (Resolve-Path (Join-Path $grammarDir "..\Generated")).Path
$cacheDir = Join-Path $env:LOCALAPPDATA "InfinityBrowser\antlr"
$jarName = "antlr-$antlrVersion-complete.jar"
$jarPath = Join-Path $cacheDir $jarName
$jarUri = "https://www.antlr.org/download/$jarName"

if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    throw "Java is required to regenerate ShaderLab.g4 with ANTLR $antlrVersion."
}

if (-not (Test-Path $jarPath)) {
    New-Item -ItemType Directory -Force -Path $cacheDir | Out-Null
    Write-Host "Downloading $jarUri"
    Invoke-WebRequest -Uri $jarUri -OutFile $jarPath
}

$java = Get-Command java
& $java.Source -jar $jarPath -Dlanguage=CSharp -visitor -no-listener -o $generatedDir -package SharpShader.ShaderLab.Frontend.Generated (Join-Path $grammarDir "ShaderLab.g4")
if ($LASTEXITCODE -ne 0) {
    throw "ANTLR $antlrVersion regeneration failed with exit code $LASTEXITCODE."
}

Write-Host "Regenerated ShaderLab sources in $generatedDir with ANTLR $antlrVersion."
