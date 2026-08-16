using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderKeywordGroup
    {
        public IReadOnlyList<string> Keywords { get; }

        public ShaderKeywordGroup(IEnumerable<string> keywords)
        {
            Keywords = keywords?.ToArray() ?? Array.Empty<string>();
        }
    }
}
