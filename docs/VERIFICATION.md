# Verification

Run these commands from the SharpShader repository with the .NET 10 SDK. The
public and internal harnesses are separate: public tests exercise the supported
surface, while the internal harness keeps compiler lifetime and native seams
source linked without shipping those test-only APIs.

Configure `stack.local.props` from the InfinityStack handoff first. The commands
below keep every output and intermediate file in one disposable product root;
they also make the source graph explicit so an ignored local package setting
cannot silently switch this run to a mixed graph.

```powershell
$productRoot = Join-Path $PWD "artifacts/verification"
$sourceProps = @(
    "-p:StackReferenceMode=Source",
    "-p:StackProductRoot=$productRoot",
    "-p:StackLocalProps=$(Join-Path $PWD 'stack.local.props')",
    "-p:RestoreUseStaticGraphEvaluation=false",
    "-p:NuGetAudit=false"
)

dotnet build src/SharpShader/SharpShader.csproj -c Debug -p:Platform=x64 @sourceProps
dotnet build src/SharpShader/SharpShader.csproj -c Release -p:Platform=x64 @sourceProps
dotnet test tests/SharpShader.Tests/SharpShader.Tests.csproj -c Debug -p:Platform=x64 @sourceProps
dotnet test tests/SharpShader.Tests/SharpShader.Tests.csproj -c Release -p:Platform=x64 @sourceProps
dotnet test tests/SharpShader.Internal.Tests/SharpShader.Internal.Tests.csproj -c Debug -p:Platform=x64 @sourceProps
dotnet test tests/SharpShader.Internal.Tests/SharpShader.Internal.Tests.csproj -c Release -p:Platform=x64 @sourceProps
$env:INFINITYSTACK_SHARPSHADER_ROOT = $PWD.Path
dotnet run --project samples/CompileAndReflect/CompileAndReflect.csproj -c Release -p:Platform=x64 @sourceProps
dotnet pack src/SharpShader/SharpShader.csproj -c Release -p:Platform=x64 -o artifacts/packages @sourceProps
```

The current Windows x64 source gates are public Debug 118/118, public Release
118/118, and internal Debug/Release 113/113. The package validation test packs
the current source, restores it into an isolated cache, and compiles a fresh
consumer that resolves `AttachmentABI.hlsl`. Native asset hashes are checked
against `native/assets.json` before build and pack. macOS and Linux native
runtime qualification requires a matching host and remains `BLOCKED_PLATFORM`
until those commands are run there.

On Windows, the Metal Shader Converter boundary validates the PE image before
launching a configured executable. A malformed or non executable file therefore
fails quickly with `ToolLaunchFailed`; a valid installed converter is exercised
by the Metal compiler tests.
