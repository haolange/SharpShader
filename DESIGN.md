# SharpShader product design

SharpShader builds independently of Infinity Engine. Product sources live under src, owned tests under tests, tools under tools and examples under samples. See docs/provenance/extraction.json and commit-map.txt for original source and preserved history.

The runtime baseline is .NET 10; compiler generators retain their appropriate netstandard target. Source and Package dependency modes are explicit and graph-wide. Local source paths belong only in ignored stack.local.props. Package dependency versions are owned by project configuration and mode-specific NuGet lock files. A consuming workspace owns its source revision manifest; the Infinity Engine integration records all six checkout SHAs in its root stack.lock.json. Products do not duplicate that workspace manifest or record their own commit inside themselves. Missing dependencies must fail rather than fall back to another version.

Output and intermediate paths are isolated by project, platform, RID, configuration and SDK target framework. Native packages use runtimes/<rid>/native; host integrations select their explicit deployment layout. Products must not infer Infinity Engine location or a developer drive from the current directory.

Public native-backed operations enforce platform and ownership boundaries. No capability downgrade or compatibility implementation is permitted to conceal unsupported execution. Current extraction acceptance is tracked by InfinityBrowser TASK-20260907-INFINITYSTACK-EXTRACTION; this document is not a claim that migration gates have passed.

Native compilation resolves standard NuGet RID assets by default. Hosts with a custom deployment layout call SharpShaderNativeLibraries.Configure with absolute DXC and SPIRV-Cross directories before first use. DXIL must accompany DXC. The configuration freezes at the first toolchain request; loading never searches the current directory or walks parent directories. Toolchain identity and locked-file hash validation remain mandatory.

The C# source generator translates reachable Shader declarations in the host compilation. Ordinary host diagnostics remain Roslyn-owned, including references resolved by generation. Standalone translation requests retain full input diagnostics. A source analyzer ProjectReference returns the generator plus Frontend and ShaderLib dependency paths; NuGet distributes the same three assemblies under analyzers/dotnet/cs. The generator is a build-time asset, not an application runtime dependency.

Product assemblies grant no friend access to Infinity Engine tests. Internal compiler seams and collision injection are verified in the source-linked internal harness; public consumers and IE use the published translation, manifest, adapter and artifact contracts. Internal tests are not shipped as product dependencies.

Product CI owns its explicit project/test inventory and dependency-only pins
in eng/ci.json. Hosted build and real-device qualification are distinct
results. Source CI cannot stand in for package consumption or another target
platform. The consuming workspace continues to own its stack revision manifest.

Application notice deployment uses ThirdPartyNotices/<product>/... for both
source and package consumers. Source Content metadata and package buildTransitive
Content items copy the same license inputs during build and publish. Native DLL
and Agility locations remain separate. Host staging must preserve these notice
files; a license inside the nupkg cache alone does not satisfy this contract.

NuGet locks are named packages.<StackReferenceMode>.<RID-or-portable>.lock.json beside each project. Source and Package modes do not share resolution state. NuGet owns TFM sections within each lock. Existing Configuration/Platform variants do not change package references. Reviewed locks are committed; verification uses RestoreLockedMode. Unsuffixed locks are retired.

CI checks out only this product's build/test dependency closure: SharpMath, SharpMetal and the GPU/Shader peer needed by integration tests. Neural and LLM are not checkout prerequisites for this product. Runtime product dependency direction remains unchanged.
