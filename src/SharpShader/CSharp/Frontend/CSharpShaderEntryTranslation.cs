using System;

namespace SharpShader.CSharp.Frontend
{
    public sealed class CSharpShaderEntryTranslation
    {
        public CSharpShaderEntryTranslation(
            string name,
            CSharpShaderStage stage,
            CSharpShaderThreadGroup? threadGroup,
            int colorTargetCount,
            bool usesRayQuery)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Entry name must not be empty.", nameof(name));
            }

            if (!Enum.IsDefined(typeof(CSharpShaderStage), stage))
            {
                throw new ArgumentOutOfRangeException(nameof(stage), stage, "Stage is not defined.");
            }

            if (colorTargetCount < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(colorTargetCount),
                    colorTargetCount,
                    "Color target count must be non-negative.");
            }

            Name = name;
            Stage = stage;
            ThreadGroup = threadGroup;
            ColorTargetCount = colorTargetCount;
            UsesRayQuery = usesRayQuery;
        }

        public string Name { get; }
        public CSharpShaderStage Stage { get; }
        public CSharpShaderThreadGroup? ThreadGroup { get; }
        public int ColorTargetCount { get; }
        public bool UsesRayQuery { get; }
    }
}
