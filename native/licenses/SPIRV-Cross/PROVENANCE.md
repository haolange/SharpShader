# SPIRV-Cross packaged native provenance

Verified 2026-09-09. The six runtime assets in native/assets.json are byte-identical to both Silk.NET.SPIRV.Cross.Native 2.23.0 and the tracked binaries at dotnet/Silk.NET tag v2.23.0, commit 94605142f7b7bd6e69c9201e8e721d245c69eb7e.

The original nuspec combines RepositoryUrl=KhronosGroup/SPIRV-Cross with the Silk.NET packaging commit. That commit belongs to https://github.com/dotnet/Silk.NET/commit/94605142f7b7bd6e69c9201e8e721d245c69eb7e . Earlier lookups against the Khronos repository did not establish a missing source; the original PACKAGE-METADATA.xml is preserved unchanged as publisher evidence.

At that Silk.NET commit, build/submodules/SPIRV-Cross points to KhronosGroup/SPIRV-Cross commit 998146d76fc5cbb2726f44c55e25fa28a573a782. build/nuke/Native/SPIRVCross.cs records a Zig ReleaseFast shared-library recipe, enabling the GLSL, HLSL, MSL, CPP and reflection C APIs, exporting C symbols, and linking libc/libc++. This is a recorded source/build configuration, not a claim of a freshly reproduced binary build. Upstream tracked binaries are the exact preservation baseline; no native bytes were replaced.

The package declares Apache-2.0. Upstream's LICENSE and LICENSES are preserved under upstream/ with their original text. Source headers contain authorship and component-specific declarations; SOURCE-HEADERS.txt preserves headers of the recipe's C++ compilation units. Including upstream's license collection does not assert every documentation license applies to this binary. The package archive itself has no standalone NOTICE. The pinned Silk workflow specifies Zig 0.15.2. Its libc++, libc++abi and MinGW license materials are preserved under upstream/toolchain; these describe the toolchain components, not a claim of a fresh linked-object audit. The source identity, build recipe and six-asset byte identity are resolved; byte-reproducible rebuilding remains unexecuted.

Archive SHA256: 5c8ea3fcf588584950be018cd47e728ac2d7d4cbbe07b205c0cc6a47c53a06a9.
See the repository file docs/provenance/native-origin-audit.json for machine-readable identity, capabilities and qualification boundaries. macOS DXC is a separate unresolved existing payload. Other-platform runtime qualification remains pending matching hardware.
