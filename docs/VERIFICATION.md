# SharpShader verification

## Checkout setup and evidence retention

Copy `stack.local.props.example` to `stack.local.props` and adjust dependency paths before Source gates. The template assumes sibling repositories and contains no developer-specific drive paths. Existing local maps are preserved. Use an explicit package feed for Package gates; do not rely on a previously populated isolated cache.

On 2026-09-11, local build outputs, package inspection caches and raw historical run evidence were intentionally deleted. Results below are historical summaries tied to their original inputs; old `artifacts/` and `.verification/` paths are reproducible output locations, not retained deliverables. Build and pack the required dependency versions before running examples that consume a local feed. Do not mark a new checkout or platform verified based on those summaries.

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

Generator acceptance must compile an external application that references SharpSLGenerated.EntryCount and Hlsl, in both Source and Package modes. Use a fresh package cache and `-p:UseSharedCompilation=false` to avoid a previously loaded analyzer dependency masking the current closure. A source ProjectReference uses OutputItemType="Analyzer" and ReferenceOutputAssembly="false"; its dependency target supplies Frontend and ShaderLib. Run the resulting application and inspect output to ensure the generator itself is absent from runtime deployment. Unit driver tests alone do not qualify analyzer assembly loading. The host-diagnostics regression suite covers references in the same and separate source trees, invalid entry/helper semantics, and unrelated host errors remaining visible to the compiler.

The earlier 118/118 source snapshot predates this repair. The repaired source suite is 123/123 in Debug and Release; internal tests remain 113/113 each. External six-product Source and fresh-cache Package consumers both compile without warnings/errors and run from C:/Windows with a generated entry and DX12/Vulkan workloads. Evidence is recorded by the consuming workspace TASK-20260907-INFINITYSTACK-EXTRACTION, under shader-generator-host-fix and the combined consumer fixture directories. Broader integration and matching-platform qualification remain separate gates.

The extraction hygiene pass removes all six Infinity.Rendering.Tests friend grants. The original IE signature-collision regression is now ShaderLayoutCollisionTests in the internal harness; Debug and Release pass 114/114. The migrated CLI/package/adapter classes pass 23/23 in Debug with the current no-friend assemblies. IE retains its own fourteen canonical graphics-pass and Hybrid asset integration tests; product-internal reflection assertions stay here. Removing friend metadata changes package assemblies and requires repacking and downstream package revalidation before final acceptance.

## Windows source graph CI

The product-owned `eng/ci.json` lists the explicit build/test projects and
pins dependency commits only. The product's own checkout is not pinned inside
itself. `eng/Verify.ps1` accepts a directory containing named dependency
checkouts, validates their identities/HEADs, writes an isolated source mapping,
and restores with locked mode. It never updates a checkout or tracked lock.

```powershell
./eng/Verify.ps1 -Configuration Debug -Gate Build -DependencyRoot /path/to/checkouts -OutputRoot "$env:TEMP/graph-build-debug"
./eng/Verify.ps1 -Configuration Release -Gate Runtime -DependencyRoot /path/to/checkouts -OutputRoot "$env:TEMP/graph-runtime-release"
```

Repeat each selected gate in both configurations using fresh output directories.
Build compiles the listed tests, tools and samples but reports runtime NOT_RUN.
Runtime additionally runs every listed test project and requires one nonempty
TRX per project, with all tests passed and no skipped results. SharpNeural
explicitly enables its Vulkan target-face gate during Runtime validation.
The script restores modified process environment variables on exit.

The GitHub workflow runs Build on hosted Windows runners. Runtime is an
explicit workflow_dispatch on main using a trusted `infinitystack-gpu` Windows
x64 runner with the documented DX12/Vulkan devices and native prerequisites.
It is not run on pull requests. Apple, Linux and mobile qualification remain
separate matching-platform work. Remote execution is TODO(UNVERIFIED).

The Source entry reports its own source qualification. Package qualification is recorded separately by VerifyPackage.ps1; the trusted runtime workflow now requires both steps. Remote end-to-end workflow qualification is still TODO(UNVERIFIED). Native
assets and pinned dependency revisions must be available in the remote
checkouts, and clean-checkout lock convergence is still required before remote
qualification. No packages or native payloads are uploaded by this workflow.

The earlier ci-graph-shader-release run failed with NU1004 because it shared a Package lock. The mode-isolated lock migration is now applied; CompileAndReflect Source locked restore, Release build and actual DXIL execution pass. The final pinned CI matrix is a separate gate and must be rerun against its selected commits.

## Isolated package runtime CI

`eng/VerifyPackage.ps1` consumes an explicit complete package feed. It copies
this product's real sample into an isolated application, clears inherited
build configuration/feed/fallback configuration, uses a fresh package cache,
restores again in locked mode, and rejects all project dependencies. Every
resolved nupkg must exist in the selected feed and its cached SHA-256 must
match that file. SDK implicit library-packs may still be probed by restore;
the explicit per-package hash requirement prevents qualifying a package absent
from the selected feed. Logs, resolved package hashes and result are retained.

```powershell
./eng/VerifyPackage.ps1 -Configuration Debug -PackageFeed /path/to/complete/feed -OutputRoot "$env:TEMP/package-debug"
./eng/VerifyPackage.ps1 -Configuration Release -PackageFeed /path/to/complete/feed -OutputRoot "$env:TEMP/package-release"
```

The trusted-device workflow runs this after its Source Runtime gate; supply
`package_feed` when requesting runtime qualification. The script performs no
package upload or source checkout. It validates the supplied feed's behavior
and records hashes, but does not assert that those packages were built from
the current source HEAD. Producing and publishing packages from the final
locked source set, remote execution and downloaded-Release consumption remain
separate required gates. A Source failure cannot be overridden by package PASS.

Current Windows Debug/Release package entries passed using the qualified local
feed: evidence ci-package-<product>-<configuration> in
D:/Projects/InfinityStackVerification. SharpGPU checks DX12 and Vulkan compute
readback and triangle draw; Shader checks nonempty DXIL from its compiler;
Neural runs both CPU and required GPU comparisons with release assertions.
Resolved package counts are 31 for GPU, 12 for Shader and 28 for Neural, with
zero source projects. Empty-feed Shader restore was also verified to fail
NU1101 and produce no PASS result. These are runtime sample gates, not a
replacement for the complete product test matrices or platform observation.

## Application notice deployment

After the normal source or package consumer restore/build, publish that same
fixture in both configurations, preserving its graph properties and package cache:

```powershell
dotnet publish $consumerProject -c $configuration --no-restore `
  -o $publishDirectory @consumerGraphProperties
```

Compare ThirdPartyNotices/<product>/... in build and publish output against the
source/package license inputs by exact relative paths and SHA-256. Do not accept
counts alone or package-cache files as application output. Test source and package
modes separately and run the published workload outside its source directory.
The 2026-09-08 Windows notice-deployment-r2 evidence in the extraction ledger
covers both configurations, both modes and build/publish: 25 combined files
(13 SharpGPU, 12 SharpShader), with every path/hash matching; the package GPU
workload also passed from the published directory. Platform claims remain separate.

## Mode-isolated NuGet locks

After deliberately generating and reviewing packages.<StackReferenceMode>.<RID-or-portable>.lock.json, append -p:RestoreLockedMode=true to the normal restore command. Alternate Source/Package restores and compare lock hashes. New RIDs need separate locks and matching-host qualification.

## Provenance preservation qualification (2026-09-09)

The current source audit is docs/provenance/native-origin-audit.json. It resolves the SPIRV-Cross nuspec repository/commit mismatch through the exact Silk.NET v2.23.0 checkout and pinned Khronos submodule. All six native binaries match upstream tracked files. The user confirmed authorization for the existing DLL/dylib assets on 2026-09-09. Preserve the current binaries and notices; authorization and original build-recipe recovery are not pending inputs. Matching-host runtime qualification remains required and separate from authorization.

A notice-only correction package is generated from the recorded clean source commit with an explicit new PackageVersion; this does not bump runtime API or dependency versions. It is a supplemental candidate pending workspace adoption, not a silently replaced preview.1 archive. Use the package provenance manifest to select its exact version and bytes.

```powershell
# Local notice-correction candidate; Source graph and isolated output are mandatory.
dotnet pack src/SharpShader/SharpShader.csproj -c Release -p:StackReferenceMode=Source -p:PackageVersion=0.4.0-preview.3 -p:RestoreLockedMode=true -p:StackLocalProps=<absolute-local-props> -p:StackProductRoot=<new-output-root> -o <new-feed>
```

This command qualifies metadata and unchanged native contents only when followed by the isolated consumer and byte comparison recorded in the consuming workspace's provenance report. Other-platform execution remains separate. Historical test-count paragraphs above remain historical snapshots; current evidence is linked from the workspace task ledger.
