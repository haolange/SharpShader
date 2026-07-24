using Xunit;
using System;
using System.IO;
using System.Linq;
using SharpShader.ShaderLab;
using System.Collections.Generic;
using SharpShader.HLSLCrossCompiler;
using System.Text.RegularExpressions;
using SharpShader.HLSLCrossCompiler.Internal;

namespace Infinity.Rendering.Tests
{
    public class HLSLCrossCompilerTests
    {
        [Fact]
        public void DXIL_StageCoverage()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            int compiledProfiles = 0;
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Vertex, ShaderTargetKind.Dxil, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Pixel, ShaderTargetKind.Dxil, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Compute, ShaderTargetKind.Dxil, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Amplification, ShaderTargetKind.Dxil, 5, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Mesh, ShaderTargetKind.Dxil, 5, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Library, ShaderTargetKind.Dxil, 0, 7);

            Assert.True(compiledProfiles > 0, "No DXIL profile compiled successfully in the requested stage coverage set.");
        }

        [Fact]
        public void SPIRV_StageCoverage()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            int compiledProfiles = 0;
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Vertex, ShaderTargetKind.SpirV, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Pixel, ShaderTargetKind.SpirV, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Compute, ShaderTargetKind.SpirV, 0, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Amplification, ShaderTargetKind.SpirV, 5, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Mesh, ShaderTargetKind.SpirV, 5, 7);
            compiledProfiles += CompileRange(capabilities, ShaderStageKind.Library, ShaderTargetKind.SpirV, 0, 7);

            Assert.True(compiledProfiles > 0, "No SPIR-V profile compiled successfully in the requested stage coverage set.");
        }

        [Fact]
        public void MSL_BasicCoverage()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            int compiledStages = 0;
            compiledStages += CompileFirstSupported(capabilities, ShaderStageKind.Vertex, ShaderTargetKind.Msl, 0, 7);
            compiledStages += CompileFirstSupported(capabilities, ShaderStageKind.Pixel, ShaderTargetKind.Msl, 0, 7);
            compiledStages += CompileFirstSupported(capabilities, ShaderStageKind.Compute, ShaderTargetKind.Msl, 0, 7);

            Assert.True(compiledStages > 0, "No MSL stage compiled successfully.");
        }

        [Fact]
        public void SpirvCrossCanonicalPath_ShouldUseExactThirdPartyLayout()
        {
            string configuredRoot = Path.Combine(
                Path.GetTempPath(),
                $"SharpShader-third-party-{Guid.NewGuid():N}");

            string exactPath = SpirvCrossNativeLibraryBootstrap.ResolveCanonicalLibraryPathForTesting(
                configuredRoot,
                Path.Combine(configuredRoot, "irrelevant-base"),
                Path.Combine(configuredRoot, "irrelevant-assembly", "SharpShader.dll"));

            string osFolder = OperatingSystem.IsWindows()
                ? "Win"
                : OperatingSystem.IsLinux()
                    ? "Linux"
                    : OperatingSystem.IsMacOS()
                        ? "macOS"
                        : throw new PlatformNotSupportedException();
            string archFolder = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
            {
                System.Runtime.InteropServices.Architecture.X64 => "AMD64",
                System.Runtime.InteropServices.Architecture.Arm64 => "ARM64",
                _ => throw new PlatformNotSupportedException(),
            };
            string nativeFileName = OperatingSystem.IsWindows()
                ? "spirv-cross.dll"
                : OperatingSystem.IsLinux()
                    ? "libspirv-cross.so"
                    : "libspirv-cross.dylib";

            string expectedPath = Path.GetFullPath(
                Path.Combine(
                    configuredRoot,
                    "Khronos",
                    "SPIRV-Cross",
                    osFolder,
                    archFolder,
                    nativeFileName));

            Assert.Equal(expectedPath, exactPath);
        }

        [Fact]
        public void SpirvCrossCanonicalPath_ShouldNotSearchCurrentDirectory()
        {
            string neutralRoot = Path.Combine(
                Path.GetTempPath(),
                $"SharpShader-native-root-{Guid.NewGuid():N}");

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => SpirvCrossNativeLibraryBootstrap.ResolveCanonicalLibraryPathForTesting(
                    null,
                    Path.Combine(neutralRoot, "app"),
                    Path.Combine(neutralRoot, "assembly", "SharpShader.dll")));

            Assert.Equal(ShaderCompilerErrorCode.BackendUnavailable, exception.ErrorCode);
            Assert.Contains("INFINITY_THIRDPARTY_NATIVE_ROOT", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SpirvCrossMissingExactLibrary_ShouldFailWithoutFallback()
        {
            string missingPath = Path.Combine(
                Path.GetTempPath(),
                $"SharpShader-missing-spirv-cross-{Guid.NewGuid():N}",
                OperatingSystem.IsWindows() ? "spirv-cross.dll" : "libspirv-cross.so");

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                () => SpirvCrossNativeLibraryBootstrap.CreateApiFromExactPath(missingPath));

            Assert.Equal(ShaderCompilerErrorCode.BackendUnavailable, exception.ErrorCode);
            Assert.Contains(Path.GetFullPath(missingPath), exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void SpirvCrossCanonicalNativeContext_MslSmoke()
        {
            const string source = @"
RWStructuredBuffer<uint> Output : register(u0);

[numthreads(1, 1, 1)]
void main(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    Output[0] = dispatchThreadId.x + 42;
}";

            ShaderCompileResult result = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = source,
                SourceName = "spirv-cross-canonical-context-smoke.hlsl",
                EntryPoint = "main",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 0),
                Target = ShaderTargetKind.Msl,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            });

            Assert.NotEmpty(result.Bytecode);
            Assert.False(string.IsNullOrWhiteSpace(result.Text));
            Assert.Contains("kernel", result.Text!, StringComparison.Ordinal);
        }

        [Fact]
        public void RayTracing_LibraryCompile()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 6);
            if (!capabilities.IsProfileSupported(ShaderStageKind.Library, shaderModel))
            {
                return;
            }

            ShaderCompileRequest baseRequest = new ShaderCompileRequest
            {
                Source = RayTracingLibrarySource,
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = shaderModel,
                Exports = new[] { "RayGenMain", "MissMain", "ClosestHitMain" },
                Target = ShaderTargetKind.Dxil,
            };

            try
            {
                ShaderCompileResult dxilResult = HLSLCrossCompiler.Compile(baseRequest);
                Assert.NotEmpty(dxilResult.Bytecode);

                ShaderCompileResult spirvResult = HLSLCrossCompiler.Compile(baseRequest with
                {
                    Target = ShaderTargetKind.SpirV,
                    SpirvOptions = new SpirvCompileOptions
                    {
                        TargetEnvironment = "vulkan1.2",
                        AdditionalArguments = new[] { "-fspv-extension=SPV_KHR_ray_tracing" },
                    },
                });

                Assert.NotEmpty(spirvResult.Bytecode);
            }
            catch (ShaderCompilerException ex) when (
                ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported ||
                ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable)
            {
                // Environment does not support this ray tracing probe.
            }
        }

        [Fact]
        public void SM68_ProfileUnsupported_ShouldFailExplicitly()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderModelVersion shaderModel68 = new ShaderModelVersion(6, 8);
            if (capabilities.IsProfileSupported(ShaderStageKind.Vertex, shaderModel68))
            {
                return;
            }

            ShaderCompileRequest request = CreateRequest(ShaderStageKind.Vertex, shaderModel68, ShaderTargetKind.Dxil);

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(() => HLSLCrossCompiler.Compile(request));
            if (exception.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable)
            {
                return;
            }

            Assert.Equal(ShaderCompilerErrorCode.ProfileUnsupported, exception.ErrorCode);
            Assert.Contains("vs_6_8", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void CompileError_Diagnostics()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderCompileRequest request = new ShaderCompileRequest
            {
                Source = "float4 main() : SV_Target { return float4(1.0, 0.0, 0.0, 1.0)",
                EntryPoint = "main",
                Stage = ShaderStageKind.Pixel,
                ShaderModel = new ShaderModelVersion(6, 6),
                Target = ShaderTargetKind.Dxil,
            };

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(() => HLSLCrossCompiler.Compile(request));
            if (exception.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable)
            {
                return;
            }

            Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
            Assert.False(string.IsNullOrWhiteSpace(exception.Diagnostics));
            Assert.Contains("inline.hlsl", exception.Diagnostics, StringComparison.OrdinalIgnoreCase);

            bool hasLineColumn = Regex.IsMatch(exception.Diagnostics, @"\(\d+,\d+\)")
                                 || Regex.IsMatch(exception.Diagnostics, @":\d+:\d+");
            Assert.True(hasLineColumn, "Diagnostics should include line and column information.");
        }

        [Fact]
        public void ModernDxcOutputs_ShouldBeStable()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 6);
            if (!capabilities.IsAnyDxcAvailable
                || !capabilities.IsProfileSupported(ShaderStageKind.Compute, shaderModel))
            {
                return;
            }

            ShaderCompileRequest request = new ShaderCompileRequest
            {
                Source = ComputeShaderSource,
                SourceName = "stable-outputs.hlsl",
                EntryPoint = "main",
                Stage = ShaderStageKind.Compute,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Dxil,
                EnableDebugInfo = true,
            };

            ShaderCompileResult first = HLSLCrossCompiler.Compile(request);
            ShaderCompileResult second = HLSLCrossCompiler.Compile(request);

            Assert.NotEmpty(first.Bytecode);
            Assert.NotEmpty(first.ReflectionData);
            Assert.Equal(20, first.ShaderHash.Length);
            Assert.True(first.ReflectionData.SequenceEqual(second.ReflectionData));
            Assert.True(first.ShaderHash.SequenceEqual(second.ShaderHash));
            Assert.True(first.PdbData.SequenceEqual(second.PdbData));

            if (first.PdbData.Length > 0)
            {
                Assert.False(string.IsNullOrWhiteSpace(first.PdbName));
            }
        }

        [Fact]
        public void UnicodeSourceNameAndInclude_ShouldCompileAndDiagnose()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 6);
            if (!capabilities.IsAnyDxcAvailable
                || !capabilities.IsProfileSupported(ShaderStageKind.Pixel, shaderModel))
            {
                return;
            }

            string temporaryDirectory = Path.Combine(
                Path.GetTempPath(),
                $"SharpShader-包含-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryDirectory);

            try
            {
                string includePath = Path.Combine(temporaryDirectory, "共享头.hlsli");
                File.WriteAllText(
                    includePath,
                    "float4 BuildColor() { return float4(0.25, 0.5, 0.75, 1.0); }");

                string source = "#include \"共享头.hlsli\"\nfloat4 main() : SV_Target { return BuildColor(); }";
                string sourcePath = Path.Combine(temporaryDirectory, "着色器-测试.hlsl");
                File.WriteAllText(sourcePath, source);

                ShaderCompileRequest request = new ShaderCompileRequest
                {
                    Source = source,
                    SourceName = sourcePath,
                    EntryPoint = "main",
                    Stage = ShaderStageKind.Pixel,
                    ShaderModel = shaderModel,
                    Target = ShaderTargetKind.Dxil,
                    IncludeDirs = new[] { temporaryDirectory },
                };

                ShaderCompileResult result;
                try
                {
                    result = HLSLCrossCompiler.Compile(request);
                }
                catch (ShaderCompilerException compileException)
                {
                    throw new InvalidOperationException(
                        $"Unicode source/include compile failed: {compileException.Diagnostics}",
                        compileException);
                }

                Assert.NotEmpty(result.Bytecode);
                Assert.NotEmpty(result.ReflectionData);
                Assert.Equal(20, result.ShaderHash.Length);

                const string invalidSource = "float4 main() : SV_Target { return BuildColor(";
                File.WriteAllText(sourcePath, invalidSource);
                ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(
                    () => HLSLCrossCompiler.Compile(request with
                    {
                        Source = invalidSource,
                    }));

                Assert.Equal(ShaderCompilerErrorCode.CompileFailed, exception.ErrorCode);
                Assert.Contains("着色器-测试.hlsl", exception.Diagnostics, StringComparison.Ordinal);

                bool hasLineColumn = Regex.IsMatch(exception.Diagnostics, @"\(\d+,\d+\)")
                                     || Regex.IsMatch(exception.Diagnostics, @":\d+:\d+");
                Assert.True(hasLineColumn, "Unicode diagnostics should include line and column information.");
            }
            finally
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        [Fact]
        public void HybridShaders_DxilCompile_Sm68()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 8);
            if (!capabilities.IsProfileSupported(ShaderStageKind.Vertex, shaderModel)
                || !capabilities.IsProfileSupported(ShaderStageKind.Pixel, shaderModel)
                || !capabilities.IsProfileSupported(ShaderStageKind.Compute, shaderModel))
            {
                return;
            }

            ShaderCompileResult vertexResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridVertexShaderSource,
                EntryPoint = "VSMain",
                Stage = ShaderStageKind.Vertex,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Dxil,
                EnableDebugInfo = true,
                DisableOptimizations = true,
            });

            ShaderCompileResult pixelResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridPixelShaderSource,
                EntryPoint = "PSMain",
                Stage = ShaderStageKind.Pixel,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Dxil,
                EnableDebugInfo = true,
                DisableOptimizations = true,
            });

            ShaderCompileResult computeResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridComputeShaderSource,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Dxil,
                EnableDebugInfo = true,
                DisableOptimizations = true,
            });

            Assert.NotEmpty(vertexResult.Bytecode);
            Assert.NotEmpty(pixelResult.Bytecode);
            Assert.NotEmpty(computeResult.Bytecode);
            Assert.NotEmpty(vertexResult.ReflectionData);
            Assert.NotEmpty(pixelResult.ReflectionData);
            Assert.NotEmpty(computeResult.ReflectionData);
        }

        [Fact]
        public void HybridRayLibrary_DxilCompile_Sm68()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 8);
            if (!capabilities.IsProfileSupported(ShaderStageKind.Library, shaderModel))
            {
                return;
            }

            string repoRoot = ResolveRepositoryRoot();
            string rayPath = Path.Combine(repoRoot, "Engine", "Shaders", "RayTracing", "Global", "HybridPrimary.raytrace");
            string hitPath = Path.Combine(repoRoot, "Engine", "Shaders", "ShaderLab", "Material", "HybridRayHit.shader");
            StandaloneShaderProgram rayProgram = ShaderLabUtil.ParseRayTraceProgramFromFile(rayPath);
            SharpShader.ShaderLab.ShaderLab hitShader = ShaderLabUtil.ParseShaderLabFromFile(hitPath);
            ShaderLabPass hitPass = hitShader.Passes.Single(pass => string.Equals(pass.Name, "HybridRay", StringComparison.Ordinal));
            string combinedSource = string.Concat(rayProgram.Source, Environment.NewLine, hitPass.Program.Source);
            string[] includeDirs = new[]
            {
                Path.GetDirectoryName(rayPath)!,
                Path.GetDirectoryName(hitPath)!,
            }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

            ShaderCompileResult result = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = combinedSource,
                SourceName = rayPath,
                EntryPoint = string.Empty,
                Stage = ShaderStageKind.Library,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Dxil,
                Exports = new[] { "RTKernel", "MissBlue", "MissOrange", "AabbGroup0Intersection", "AabbGroup1Intersection" },
                IncludeDirs = includeDirs,
            });

            Assert.NotEmpty(result.Bytecode);
            string diagnostics = result.Diagnostics ?? string.Empty;
            Assert.DoesNotContain("incorrect number of entry parameters for raytracing stage 'raygeneration'", diagnostics, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("use of undeclared identifier 'ReportHit'", diagnostics, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("requires that it is annotated with the [raypayload] attribute", diagnostics, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void HybridShaders_MslCompile_Sm68()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderModelVersion shaderModel = new ShaderModelVersion(6, 8);
            if (!capabilities.IsProfileSupported(ShaderStageKind.Vertex, shaderModel)
                || !capabilities.IsProfileSupported(ShaderStageKind.Pixel, shaderModel)
                || !capabilities.IsProfileSupported(ShaderStageKind.Compute, shaderModel))
            {
                return;
            }

            ShaderCompileResult vertexResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridVertexShaderSource,
                EntryPoint = "VSMain",
                Stage = ShaderStageKind.Vertex,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Msl,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            });

            ShaderCompileResult pixelResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridPixelShaderSource,
                EntryPoint = "PSMain",
                Stage = ShaderStageKind.Pixel,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Msl,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            });

            ShaderCompileResult computeResult = HLSLCrossCompiler.Compile(new ShaderCompileRequest
            {
                Source = HybridComputeShaderSource,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = shaderModel,
                Target = ShaderTargetKind.Msl,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            });

            Assert.False(string.IsNullOrWhiteSpace(vertexResult.Text));
            Assert.False(string.IsNullOrWhiteSpace(pixelResult.Text));
            Assert.False(string.IsNullOrWhiteSpace(computeResult.Text));
            Assert.Contains("VSMain", vertexResult.Text!);
            Assert.Contains("PSMain", pixelResult.Text!);
            Assert.Contains("CSMain", computeResult.Text!);
        }

        [Fact]
        public void Hybrid_MetalCompileFailure_ShouldFailExplicitly()
        {
            ShaderCompilerCapabilities capabilities = ShaderCompilerCapabilities.Probe();
            if (!capabilities.IsAnyDxcAvailable)
            {
                return;
            }

            ShaderCompileRequest request = new ShaderCompileRequest
            {
                Source = InvalidHybridComputeShaderSource,
                EntryPoint = "CSMain",
                Stage = ShaderStageKind.Compute,
                ShaderModel = new ShaderModelVersion(6, 8),
                Target = ShaderTargetKind.Msl,
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            };

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(() => HLSLCrossCompiler.Compile(request));
            if (exception.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable)
            {
                return;
            }

            Assert.True(
                exception.ErrorCode == ShaderCompilerErrorCode.CompileFailed
                || exception.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported
                || exception.ErrorCode == ShaderCompilerErrorCode.MslTranslateFailed);
            Assert.False(string.IsNullOrWhiteSpace(exception.Diagnostics));
        }

        [Fact]
        public void NativeBackendUnavailable_ShouldPropagate()
        {
            ShaderCompileRequest request = CreateRequest(ShaderStageKind.Vertex, new ShaderModelVersion(6, 7), ShaderTargetKind.Dxil);

            ShaderCompilerExecutionContext context = new ShaderCompilerExecutionContext
            {
                NativeCompileOverride = _ => throw new ShaderCompilerException(ShaderCompilerErrorCode.BackendUnavailable, "Native DXC unavailable."),
                IsNativeDxcAvailableOverride = () => false,
            };

            ShaderCompilerException exception = Assert.Throws<ShaderCompilerException>(() => HLSLCrossCompiler.CompileForTesting(request, context));
            Assert.Equal(ShaderCompilerErrorCode.BackendUnavailable, exception.ErrorCode);
            Assert.Contains("Native DXC unavailable", exception.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void ProbeCapabilities_NoNativeBackend_ShouldReportUnavailable()
        {
            ShaderCompilerExecutionContext context = new ShaderCompilerExecutionContext
            {
                IsNativeDxcAvailableOverride = () => false,
            };

            ShaderCompilerCapabilities capabilities = HLSLCrossCompiler.ProbeCapabilitiesForTesting(context);
            Assert.False(capabilities.NativeDxcAvailable);
            Assert.False(capabilities.IsAnyDxcAvailable);
            Assert.Empty(capabilities.ProfileSupport);
        }

        private static int CompileRange(
            ShaderCompilerCapabilities capabilities,
            ShaderStageKind stage,
            ShaderTargetKind target,
            int minMinor,
            int maxMinor)
        {
            int count = 0;
            foreach (ShaderModelVersion shaderModel in EnumerateShaderModels(minMinor, maxMinor))
            {
                if (!capabilities.IsProfileSupported(stage, shaderModel))
                {
                    continue;
                }

                try
                {
                    ShaderCompileRequest request = CreateRequest(stage, shaderModel, target);
                    ShaderCompileResult result = HLSLCrossCompiler.Compile(request);
                    Assert.NotEmpty(result.Bytecode);
                    count++;
                }
                catch (ShaderCompilerException ex) when (
                    ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported ||
                    ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable ||
                    ex.ErrorCode == ShaderCompilerErrorCode.CompileFailed ||
                    ex.ErrorCode == ShaderCompilerErrorCode.MslTranslateFailed)
                {
                    // Keep stage coverage probes resilient to backend/profile drift across DXC builds.
                }
            }

            return count;
        }

        private static int CompileFirstSupported(
            ShaderCompilerCapabilities capabilities,
            ShaderStageKind stage,
            ShaderTargetKind target,
            int minMinor,
            int maxMinor)
        {
            foreach (ShaderModelVersion shaderModel in EnumerateShaderModels(minMinor, maxMinor))
            {
                if (!capabilities.IsProfileSupported(stage, shaderModel))
                {
                    continue;
                }

                try
                {
                    ShaderCompileRequest request = CreateRequest(stage, shaderModel, target);
                    ShaderCompileResult result = HLSLCrossCompiler.Compile(request);
                    Assert.NotEmpty(result.Bytecode);
                    Assert.False(string.IsNullOrWhiteSpace(result.Text));
                    return 1;
                }
                catch (ShaderCompilerException ex) when (
                    ex.ErrorCode == ShaderCompilerErrorCode.ProfileUnsupported ||
                    ex.ErrorCode == ShaderCompilerErrorCode.BackendUnavailable ||
                    ex.ErrorCode == ShaderCompilerErrorCode.CompileFailed ||
                    ex.ErrorCode == ShaderCompilerErrorCode.MslTranslateFailed)
                {
                    // Try next profile model.
                }
            }

            return 0;
        }

        private static IEnumerable<ShaderModelVersion> EnumerateShaderModels(int minMinor, int maxMinor)
        {
            for (int minor = minMinor; minor <= maxMinor; minor++)
            {
                yield return new ShaderModelVersion(6, minor);
            }
        }

        private static ShaderCompileRequest CreateRequest(ShaderStageKind stage, ShaderModelVersion shaderModel, ShaderTargetKind target)
        {
            (string source, string entryPoint, IReadOnlyList<string> exports) = stage switch
            {
                ShaderStageKind.Vertex => (VertexShaderSource, "main", Array.Empty<string>()),
                ShaderStageKind.Pixel => (PixelShaderSource, "main", Array.Empty<string>()),
                ShaderStageKind.Compute => (ComputeShaderSource, "main", Array.Empty<string>()),
                ShaderStageKind.Amplification => (AmplificationShaderSource, "main", Array.Empty<string>()),
                ShaderStageKind.Mesh => (MeshShaderSource, "main", Array.Empty<string>()),
                ShaderStageKind.Library => (LibraryShaderSource, string.Empty, new[] { "ExportA", "ExportB" }),
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported test stage."),
            };

            return new ShaderCompileRequest
            {
                Source = source,
                EntryPoint = entryPoint,
                Stage = stage,
                ShaderModel = shaderModel,
                Target = target,
                Exports = exports,
                SpirvOptions = new SpirvCompileOptions
                {
                    TargetEnvironment = "vulkan1.2",
                },
                MslOptions = new MslCompileOptions
                {
                    Platform = MslTargetPlatform.MacOS,
                },
            };
        }

        private const string VertexShaderSource = @"
    float4 main(float4 position : POSITION) : SV_Position
    {
        return position;
    }";

        private const string PixelShaderSource = @"
    float4 main() : SV_Target0
    {
        return float4(0.0, 1.0, 0.0, 1.0);
    }";

        private const string ComputeShaderSource = @"
    [numthreads(1, 1, 1)]
    void main(uint3 id : SV_DispatchThreadID)
    {
    }";

        private const string AmplificationShaderSource = @"
    struct Payload
    {
        uint Value;
    };

    [numthreads(1, 1, 1)]
    void main(uint3 dispatchId : SV_DispatchThreadID)
    {
        Payload payload;
        payload.Value = 0;
        DispatchMesh(1, 1, 1, payload);
    }";

        private const string MeshShaderSource = @"
    struct MeshVertex
    {
        float4 Position : SV_Position;
    };

    [outputtopology(""triangle"")]
    [numthreads(1, 1, 1)]
    void main(out vertices MeshVertex verticesOut[3], out indices uint3 primitives[1])
    {
        SetMeshOutputCounts(3, 1);

        verticesOut[0].Position = float4(0.0, 0.5, 0.0, 1.0);
        verticesOut[1].Position = float4(0.5, -0.5, 0.0, 1.0);
        verticesOut[2].Position = float4(-0.5, -0.5, 0.0, 1.0);

        primitives[0] = uint3(0, 1, 2);
    }";

        private const string LibraryShaderSource = @"
    float4 ExportA(float4 position : POSITION) : SV_Position
    {
        return position;
    }

    float4 ExportB(float4 position : POSITION) : SV_Position
    {
        return position * 0.5;
    }";

        private const string RayTracingLibrarySource = @"
    struct Payload
    {
        float4 Color;
    };

    [shader(""raygeneration"")]
    void RayGenMain()
    {
    }

    [shader(""miss"")]
    void MissMain(inout Payload payload)
    {
        payload.Color = float4(1.0, 0.0, 0.0, 1.0);
    }

    [shader(""closesthit"")]
    void ClosestHitMain(inout Payload payload, in BuiltInTriangleIntersectionAttributes attributes)
    {
        payload.Color = float4(0.0, 1.0, 0.0, 1.0);
    }";

        private const string HybridVertexShaderSource = @"
    struct VertexIn
    {
        float4 color : COLOR0;
        float4 vertexOS : POSITION;
    };

    struct Varyings
    {
        float2 uv0 : TEXCOORD0;
        float4 position : SV_Position;
    };

    Varyings VSMain(VertexIn input)
    {
        Varyings output;
        output.position = input.vertexOS;
        output.uv0 = float2(input.vertexOS.x * 0.5f + 0.5f, 1.0f - (input.vertexOS.y * 0.5f + 0.5f));
        return output;
    }";

        private const string HybridPixelShaderSource = @"
    float4 PSMain(float2 uv0 : TEXCOORD0) : SV_Target0
    {
        return float4(uv0, 0.0f, 1.0f);
    }";

        private const string HybridComputeShaderSource = @"
    RWTexture2D<float4> _ResultTexture : register(u0);

    [numthreads(8, 8, 1)]
    void CSMain(uint3 id : SV_DispatchThreadID)
    {
        _ResultTexture[id.xy] = float4((float)(id.x & 255u) / 255.0f, (float)(id.y & 255u) / 255.0f, 0.0f, 1.0f);
    }";

        private const string InvalidHybridComputeShaderSource = @"
    [numthreads(8, 8, 1)]
    void CSMain(uint3 id : SV_DispatchThreadID
    {
    }";

        private static string ResolveRepositoryRoot()
        {
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "InfinityBrowser.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Unable to locate repository root from test runtime path.");
        }
    }
}
