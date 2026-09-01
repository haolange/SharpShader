using System;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation
{
    /// <summary>
    /// Compile-time wave/subgroup contract for shader targets. This is not a live
    /// <c>RHIDevice</c> fact: DXIL wave ops exist in SM 6.0+, SPIR-V exposes
    /// subgroup ops, and MSL exposes simdgroup ops.
    /// </summary>
    public static class ShaderBackendWaveContract
    {
        public static bool CanLowerWaveOperations(
            ShaderBackendKind backend,
            ShaderModelVersion shaderModel)
        {
            if (!Enum.IsDefined(backend))
            {
                throw new ArgumentOutOfRangeException(nameof(backend), backend, "Backend is not defined.");
            }

            return backend switch
            {
                ShaderBackendKind.DirectX12 => shaderModel.IsInRange,
                ShaderBackendKind.Vulkan => true,
                ShaderBackendKind.Metal => true,
                _ => false,
            };
        }

        public static bool AllRequestedTargetsSupportWaveOperations(
            ShaderProgramTarget targets,
            ShaderModelVersion shaderModel)
        {
            if (targets == ShaderProgramTarget.None
                || (targets & ~ShaderProgramTarget.All) != 0)
            {
                return false;
            }

            if ((targets & ShaderProgramTarget.DirectX12) != 0
                && !CanLowerWaveOperations(ShaderBackendKind.DirectX12, shaderModel))
            {
                return false;
            }

            if ((targets & ShaderProgramTarget.Vulkan) != 0
                && !CanLowerWaveOperations(ShaderBackendKind.Vulkan, shaderModel))
            {
                return false;
            }

            if ((targets & ShaderProgramTarget.MetalMsl) != 0
                && !CanLowerWaveOperations(ShaderBackendKind.Metal, shaderModel))
            {
                return false;
            }

            return true;
        }
    }
}
