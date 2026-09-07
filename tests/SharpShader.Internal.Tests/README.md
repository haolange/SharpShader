# Internal regression harness

This non-packable test assembly compiles the canonical core source list from
`build/SharpShader.Core.props`, also used by the shipped SharpShader assembly.
It retains deterministic cache races, injected compiler failures and native
resource lifetime tests without exporting internal APIs or adding friend assemblies.
CLI command handling is source-linked in the separate public test assembly.
No implementation is copied into tests.

The separate `SharpShader.Tests` project validates the actual product assemblies.
This harness does not replace package-consumption or platform runtime acceptance.
Authoritative commands and evidence boundaries are in `docs/VERIFICATION.md`.
