using System;

namespace SharpShader.CSharp.ShaderLib
{
    public static class SV
    {
        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class PositionAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class VertexIDAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class InstanceIDAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class DispatchThreadIDAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class GroupIDAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class GroupThreadIDAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class GroupIndexAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class IsFrontFaceAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class DepthAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class SampleIndexAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class CoverageAttribute : Attribute
        {
        }

        [AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
        public sealed class TargetAttribute : Attribute
        {
            public TargetAttribute(int index)
            {
                if (index < 0 || index > 7)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(index),
                        index,
                        "Render target index must be in [0, 7].");
                }

                Index = index;
            }

            public int Index { get; }
        }
    }
}
