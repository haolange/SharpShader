using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabPass
    {
        public string? Name
        {
            get
            {
                return Tags.TryGetValue("Name", out string? name) ? name : null;
            }
        }

        public ShaderLabProgram Program { get; }
        public ShaderLabRenderState? State { get; }
        public IReadOnlyDictionary<string, string> Tags { get; }

        public ShaderLabPass(
            ShaderLabProgram program,
            IReadOnlyDictionary<string, string> tags,
            ShaderLabRenderState? state = null)
        {
            Program = program ?? throw new ArgumentNullException(nameof(program));
            Tags = tags ?? new Dictionary<string, string>(StringComparer.Ordinal);
            State = state;
        }
    }
}
