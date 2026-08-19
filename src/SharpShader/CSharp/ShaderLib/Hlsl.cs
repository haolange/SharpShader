namespace SharpShader.CSharp.ShaderLib
{
    public static class Hlsl
    {
        [Intrinsic("ALL_MEMORY_BARRIER")]
        public static void AllMemoryBarrier()
        {
        }

        [Intrinsic("GROUP_MEMORY_BARRIER_WITH_GROUP_SYNC")]
        public static void GroupMemoryBarrierWithGroupSync()
        {
        }

        [Intrinsic("INTERLOCKED_ADD")]
        public static void InterlockedAdd<T>(ref T destination, T value)
        {
        }

        [Intrinsic("DDX")]
        public static T Ddx<T>(T value)
        {
            return default!;
        }

        [Intrinsic("DDY")]
        public static T Ddy<T>(T value)
        {
            return default!;
        }

        [Intrinsic("WAVE_ACTIVE_SUM")]
        public static T WaveActiveSum<T>(T value)
        {
            return default!;
        }
    }
}
