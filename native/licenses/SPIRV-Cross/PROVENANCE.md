# SPIRV-Cross packaged native license evidence

The six SPIRV-Cross assets listed in `native/assets.json` originate from
Silk.NET.SPIRV.Cross.Native 2.23.0, archive SHA-256
`5C8EA3FCF588584950BE018CD47E728AC2D7D4CBBE07B205C0CC6A47C53A06A9`.
`PACKAGE-METADATA.xml` contains the exact bytes of the original nuspec member, retained as the
package publisher's metadata declaration. It declares Apache-2.0 and links to
https://licenses.nuget.org/Apache-2.0.

`Apache-2.0.txt` is the unmodified standard license text retrieved from
https://www.apache.org/licenses/LICENSE-2.0.txt on 2026-09-08. SHA-256:
`CFC7749B96F63BD31C3C42B5C471BF756814053E847C10F3EB003417BC523D30`.
This file is preserved to accompany the declared license; it is not an
assertion that all copyright/NOTICE obligations have been identified.

The archive does not include a standalone license or NOTICE file. Its nuspec
declares repository https://github.com/KhronosGroup/SPIRV-Cross and commit
`94605142f7b7bd6e69c9201e8e721d245c69eb7e`. Direct raw LICENSE lookups at that
commit in KhronosGroup/SPIRV-Cross and dotnet/Silk.NET returned HTTP 404;
GitHub API commit queries were rate-limited. The declared commit is therefore
not accepted as a verified upstream build source. A missing raw path alone
does not prove that the commit is invalid.

Upstream source/build provenance and any applicable additional notices remain
TODO(UNVERIFIED). Resolve them before public redistribution. This evidence
does not qualify the separate private macOS DXC build or any platform runtime.

The metadata uses an .xml filename because NuGet excludes .nuspec payloads
from packages. Its contents remain unmodified; it is evidence, not this
product's package manifest.
