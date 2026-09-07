using System.Security.Cryptography;
using SharpShader.HLSLCrossCompiler;

const string source = """
    RWStructuredBuffer<uint> Output : register(u0);
    [numthreads(1, 1, 1)]
    void Main(uint3 dispatchThreadId : SV_DispatchThreadID)
    {
        Output[dispatchThreadId.x] = 41;
    }
    """;

ShaderCompileResult result = HLSLCrossCompiler.Compile(
    new ShaderCompileRequest
    {
        Source = source,
        SourceName = "sharpshader-sample.compute.hlsl",
        EntryPoint = "Main",
        Stage = ShaderStageKind.Compute,
        ShaderModel = new ShaderModelVersion(6, 6),
        Target = ShaderTargetKind.Dxil,
    });

if (result.Bytecode.Length == 0)
{
    throw new InvalidOperationException("SharpShader produced an empty DXIL artifact.");
}

Console.WriteLine($"target=DXIL bytes={result.Bytecode.Length} sha256={Convert.ToHexString(SHA256.HashData(result.Bytecode))}");
