# ShaderLab ANTLR grammar

Canonical grammar: `ShaderLab.g4`.

Generated C# lives in `../Generated/` and is checked in. Regenerating requires ANTLR 4.13.1 — the same version as `Antlr4.Runtime.Standard` in `SharpShader.csproj`.

```powershell
.\regenerate.ps1
```

The script downloads the official `antlr-4.13.1-complete.jar` into a local cache if it is not already present, then overwrites the generated files. Do not regenerate with any other ANTLR major/minor version.
