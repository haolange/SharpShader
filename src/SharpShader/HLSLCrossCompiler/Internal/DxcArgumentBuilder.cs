using System;
using System.Globalization;
using System.Collections.Generic;
using Silk.NET.Direct3D.Compilers;

namespace SharpShader.HLSLCrossCompiler.Internal
{
    internal static class DxcArgumentBuilder
    {
        public static string BuildProfile(ShaderStageKind stage, ShaderModelVersion shaderModel)
        {
            return $"{GetStagePrefix(stage)}_{shaderModel.Major}_{shaderModel.Minor}";
        }

        public static string GetStagePrefix(ShaderStageKind stage)
        {
            return stage switch
            {
                ShaderStageKind.Vertex => "vs",
                ShaderStageKind.Hull => "hs",
                ShaderStageKind.Domain => "ds",
                ShaderStageKind.Geometry => "gs",
                ShaderStageKind.Pixel => "ps",
                ShaderStageKind.Compute => "cs",
                ShaderStageKind.Amplification => "as",
                ShaderStageKind.Mesh => "ms",
                ShaderStageKind.Library => "lib",
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unsupported shader stage."),
            };
        }

        public static List<string> BuildArguments(ShaderCompileRequest request, string profile)
        {
            List<string> args = new List<string>
            {
                "-T", profile,
                "-HV", "2021",
            };

            if (request.Stage != ShaderStageKind.Library || !string.IsNullOrWhiteSpace(request.EntryPoint))
            {
                args.Add("-E");
                args.Add(request.EntryPoint);
            }

            if (request.Enable16BitTypes && Supports16BitTypes(request.ShaderModel))
            {
                args.Add("-enable-16bit-types");
            }

            if (request.EnableDebugInfo)
            {
                args.Add(DXC.ArgDebug);
                args.Add("-Qembed_debug");
            }

            if (request.SkipValidation)
            {
                args.Add(DXC.ArgSkipValidation);
            }

            if (request.DisableOptimizations)
            {
                args.Add(DXC.ArgSkipOptimizations);
            }
            else
            {
                args.Add(request.OptimizationLevel switch
                {
                    0 => DXC.ArgOptimizationLevel0,
                    1 => DXC.ArgOptimizationLevel1,
                    2 => DXC.ArgOptimizationLevel2,
                    _ => DXC.ArgOptimizationLevel3,
                });
            }

            if (request.TreatWarningsAsErrors)
            {
                args.Add(DXC.ArgWarningsAreErrors);
            }

            foreach (ShaderDefine define in request.Defines)
            {
                if (string.IsNullOrWhiteSpace(define.Name))
                {
                    continue;
                }

                if (string.IsNullOrEmpty(define.Value))
                {
                    args.Add($"-D{define.Name}");
                }
                else
                {
                    args.Add($"-D{define.Name}={define.Value}");
                }
            }

            foreach (string includeDir in request.IncludeDirs)
            {
                if (string.IsNullOrWhiteSpace(includeDir))
                {
                    continue;
                }

                args.Add("-I");
                args.Add(includeDir);
            }

            if (request.Stage == ShaderStageKind.Library && request.Exports.Count > 0)
            {
                args.Add("-exports");
                args.Add(string.Join(';', request.Exports));
            }

            if (request.Target == ShaderTargetKind.SpirV)
            {
                args.Add("-spirv");
                AppendSpirvOptions(args, request.SpirvOptions);
            }

            foreach (string extraArgument in request.ExtraArguments)
            {
                if (!string.IsNullOrWhiteSpace(extraArgument))
                {
                    args.Add(extraArgument);
                }
            }

            return args;
        }

        private static bool Supports16BitTypes(ShaderModelVersion shaderModel)
        {
            return shaderModel.Major > 6 || (shaderModel.Major == 6 && shaderModel.Minor >= 2);
        }

        private static void AppendSpirvOptions(List<string> args, SpirvCompileOptions options)
        {
            if (options.UseDxLayout)
            {
                args.Add("-fvk-use-dx-layout");
            }

            if (options.UseGlLayout)
            {
                args.Add("-fvk-use-gl-layout");
            }

            if (options.UseScalarLayout)
            {
                args.Add("-fvk-use-scalar-layout");
            }

            if (options.InvertY)
            {
                args.Add("-fvk-invert-y");
            }

            AppendShiftOption(args, "-fvk-t-shift", options.TextureBindingShift, options.TextureBindingSpace);
            AppendShiftOption(args, "-fvk-s-shift", options.SamplerBindingShift, options.SamplerBindingSpace);
            AppendShiftOption(args, "-fvk-u-shift", options.UavBindingShift, options.UavBindingSpace);
            AppendShiftOption(args, "-fvk-b-shift", options.CBufferBindingShift, options.CBufferBindingSpace);

            if (!string.IsNullOrWhiteSpace(options.TargetEnvironment))
            {
                args.Add($"-fspv-target-env={options.TargetEnvironment}");
            }

            foreach (string additionalArgument in options.AdditionalArguments)
            {
                if (!string.IsNullOrWhiteSpace(additionalArgument))
                {
                    args.Add(additionalArgument);
                }
            }
        }

        private static void AppendShiftOption(List<string> args, string optionName, int? shift, uint registerSpace)
        {
            if (!shift.HasValue)
            {
                return;
            }

            args.Add(optionName);
            args.Add(shift.Value.ToString(CultureInfo.InvariantCulture));
            args.Add(registerSpace.ToString(CultureInfo.InvariantCulture));
        }
    }
}
