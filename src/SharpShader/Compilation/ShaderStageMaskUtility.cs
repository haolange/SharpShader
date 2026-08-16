using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.Compilation
{

    public static class ShaderStageMaskUtility
    {
        public static ShaderStageMask FromStage(ShaderExecutionStage stage)
        {
            return stage switch
            {
                ShaderExecutionStage.Vertex => ShaderStageMask.Vertex,
                ShaderExecutionStage.Hull => ShaderStageMask.Hull,
                ShaderExecutionStage.Domain => ShaderStageMask.Domain,
                ShaderExecutionStage.Geometry => ShaderStageMask.Geometry,
                ShaderExecutionStage.Pixel => ShaderStageMask.Pixel,
                ShaderExecutionStage.Compute => ShaderStageMask.Compute,
                ShaderExecutionStage.Amplification => ShaderStageMask.Amplification,
                ShaderExecutionStage.Mesh => ShaderStageMask.Mesh,
                ShaderExecutionStage.RayGeneration => ShaderStageMask.RayGeneration,
                ShaderExecutionStage.Intersection => ShaderStageMask.Intersection,
                ShaderExecutionStage.AnyHit => ShaderStageMask.AnyHit,
                ShaderExecutionStage.ClosestHit => ShaderStageMask.ClosestHit,
                ShaderExecutionStage.Miss => ShaderStageMask.Miss,
                ShaderExecutionStage.Callable => ShaderStageMask.Callable,
                ShaderExecutionStage.Node => ShaderStageMask.Node,
                _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Shader stage is not defined."),
            };
        }

        public static ShaderStageMask Union(IEnumerable<ShaderExecutionStage> stages)
        {
            ArgumentNullException.ThrowIfNull(stages);

            ShaderStageMask result = ShaderStageMask.None;
            foreach (ShaderExecutionStage stage in stages)
            {
                result |= FromStage(stage);
            }

            return result;
        }

        public static void Validate(ShaderStageMask stages, bool allowNone = false)
        {
            if ((stages & ~ShaderStageMask.All) != 0 || (!allowNone && stages == ShaderStageMask.None))
            {
                throw new ArgumentOutOfRangeException(nameof(stages), stages, "Shader stage mask is empty or contains undefined bits.");
            }
        }
    }
}
