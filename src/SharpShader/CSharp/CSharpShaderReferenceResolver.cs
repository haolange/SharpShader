using System;
using System.Collections.Generic;
using System.IO;
using SharpMath;
using SharpShader.CSharp.ShaderLib;

namespace SharpShader.CSharp
{
    public static class CSharpShaderReferenceResolver
    {
        public static IReadOnlyList<string> ResolveDefaultReferences()
        {
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> paths = new List<string>();

            string? trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (!string.IsNullOrWhiteSpace(trusted))
            {
                foreach (string candidate in trusted.Split(Path.PathSeparator))
                {
                    string name = Path.GetFileName(candidate);
                    if (IsRuntimeAssembly(name) && unique.Add(candidate))
                    {
                        paths.Add(candidate);
                    }
                }
            }

            Add(unique, paths, typeof(object).Assembly.Location);
            Add(unique, paths, typeof(RWStructuredBuffer<float>).Assembly.Location);
            Add(unique, paths, typeof(float3).Assembly.Location);
            Add(unique, paths, typeof(Attribute).Assembly.Location);

            return paths;
        }

        private static void Add(HashSet<string> unique, List<string> paths, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !unique.Add(path))
            {
                return;
            }

            paths.Add(path);
        }

        private static bool IsRuntimeAssembly(string name)
        {
            return name.Equals("mscorlib.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Private.CoreLib.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Runtime.Extensions.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Runtime.InteropServices.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Runtime.CompilerServices.Unsafe.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Console.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Linq.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Collections.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Memory.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Numerics.dll", StringComparison.OrdinalIgnoreCase)
                || name.Equals("System.Threading.dll", StringComparison.OrdinalIgnoreCase);
        }
    }
}
