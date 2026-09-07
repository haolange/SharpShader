# SharpShader verification

This file is the authority for the independent SharpShader repository. The
public and internal harnesses are separate: public tests exercise the supported
surface, while the internal harness keeps compiler lifetime and native seams
source linked without shipping test-only APIs. All commands use the .NET 10 SDK
and select `Source` or `Package` for the complete graph. `stack.local.props` is
an ignored machine path mapping and the consuming workspace revision manifest is the shareable revision
set.

```powershell
$productRoot = Join-Path $PWD "artifacts/verification-r12"
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

The current Windows x64 source gates are green: public Debug and Release are
**118/118**, and internal Debug and Release are **113/113**. The Release
`CompileAndReflect` sample was run from `C:\Windows`; it produced a 3028-byte
DXIL target (`sha256=286A23CF...`) and resolved the generated attachment ABI.
The seven source packages are `SharpShader`, `SharpShader.CSharp`,
`SharpShader.CSharp.Frontend`, `SharpShader.CSharp.Generators`,
`SharpShader.CSharp.ShaderLib`, `SharpShader.SharpGPU`, and
`SharpShader.Tool`.

Package validation restores a fresh Release/x64 assets file into the isolated
`artifacts/verification-r12/nuget-cache-package-shader-final` cache using the
final local SharpShader feed (`packages-release-final`) plus SharpGPU, SharpMath,
SharpMetal and IE feeds. The public package graph passed **117/117** and the
package `CompileAndReflect` sample passed with the same DXIL output. The
matching logs are `artifacts/verification-r12/test-public-package-release-final.log`
and `sample-package-release-final.log`. Always pass `Configuration`, `Platform`,
`StackReferenceMode` and `RestorePackagesPath` together; a global-cache or
different-configuration assets file is not package evidence.

Native asset hashes are checked against `native/assets.json` before build and
pack. The `SharpShader.SharpGPU` adapter depends on the independent SharpGPU
package; compiler core does not reverse-reference SharpGPU.

macOS and Linux native runtime qualification requires matching hosts and
remains `BLOCKED_PLATFORM` / `TODO(UNVERIFIED)` until those commands run there.

On Windows, the Metal Shader Converter boundary validates the PE image before
launching a configured executable. A malformed or non executable file therefore
fails quickly with `ToolLaunchFailed`; a valid installed converter is exercised
by the Metal compiler tests. Before accepting a revision, run `git diff --check`,
inspect generated package dependencies and native paths, and update
the consuming workspace revision manifest after the final commit. Any source or package change
invalidates the corresponding evidence.

Source handoff records this repository HEAD and every mapped dependency HEAD in
the consuming workspace manifest. IE uses its root stack.lock.json; standalone
consumers own their manifest and do not need an IE checkout. Package consumers
use the project dependency versions and NuGet lock files. There is no product-local
stack.lock.json: the removed copies were not read by any build or setup tool.
