using System;
using SharpShader.Compilation;
using SharpShader.HLSLCrossCompiler;
using Rhi = global::SharpGPU;

namespace SharpShader.SharpGPU
{
    /// <summary>
    /// Leaf adapter over SharpGPU Compute wave / cooperative-matrix facts.
    /// Wave lowering already exists as <c>WaveActiveSum</c> (HLSL SM 6.0+ /
    /// SPIR-V subgroup / MSL simdgroup). No cooperative-matrix / WaveMMA
    /// shader AST exists; DX12 emission is fail-closed because HAL
    /// <c>Compute.CooperativeMatrix</c> is Unavailable (vendored SDK has no query).
    /// </summary>
    public static class SharpGpuWaveAndCooperativeMatrixContract
    {
        public static bool CanLowerWaveOperations(
            Rhi.ERHIBackend backend,
            ShaderModelVersion shaderModel)
        {
            return ShaderBackendWaveContract.CanLowerWaveOperations(
                MapBackend(backend),
                shaderModel);
        }

        public static bool CanUseWaveOperations(Rhi.RHIDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            return device.Capabilities.Compute.WaveOperations.Tier
                != Rhi.ERHICapabilityTier.Unavailable;
        }

        public static void RequireWaveOperations(Rhi.RHIDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            device.Capabilities.Compute.WaveOperations.Require(
                "Compute.WaveOperations");
        }

        /// <summary>
        /// Compile-time backend possibility, not a live device probe.
        /// Vulkan KHR and Metal simdgroup matrix may exist; DX12 WaveMMA must
        /// not be emitted because HAL reports Unavailable for every DX12 device.
        /// </summary>
        public static bool CanLowerCooperativeMatrix(Rhi.ERHIBackend backend)
        {
            return backend switch
            {
                Rhi.ERHIBackend.Vulkan => true,
                Rhi.ERHIBackend.Metal => true,
                Rhi.ERHIBackend.DirectX12 => false,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    backend,
                    "Backend is not a SharpGPU graphics backend."),
            };
        }

        public static bool CanUseCooperativeMatrix(Rhi.RHIDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            return device.Capabilities.Compute.CooperativeMatrix.Tier
                != Rhi.ERHICapabilityTier.Unavailable;
        }

        public static void RequireCooperativeMatrixLowering(Rhi.ERHIBackend backend)
        {
            if (CanLowerCooperativeMatrix(backend))
            {
                return;
            }

            throw new NotSupportedException(
                "SharpShader will not emit DirectX12 WaveMMA / cooperative-matrix "
                + "code. HAL Compute.CooperativeMatrix is Unavailable because the "
                + "vendored SDK has no WaveMMA query, and no cooperative-matrix "
                + "shader AST exists to lower.");
        }

        public static void RequireCooperativeMatrix(Rhi.RHIDevice device)
        {
            ArgumentNullException.ThrowIfNull(device);
            device.Capabilities.Compute.CooperativeMatrix.Require(
                "Compute.CooperativeMatrix");
        }

        private static ShaderBackendKind MapBackend(Rhi.ERHIBackend backend)
        {
            return backend switch
            {
                Rhi.ERHIBackend.DirectX12 => ShaderBackendKind.DirectX12,
                Rhi.ERHIBackend.Vulkan => ShaderBackendKind.Vulkan,
                Rhi.ERHIBackend.Metal => ShaderBackendKind.Metal,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(backend),
                    backend,
                    "Backend is not a SharpGPU graphics backend."),
            };
        }
    }
}
