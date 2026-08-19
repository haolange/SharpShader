namespace SharpShader.CSharp.ShaderLib
{
    public struct RayDesc
    {
        public float TMin;
        public float TMax;

        [Intrinsic("RAYDESC_FROM")]
        public static RayDesc From<TVector>(TVector origin, TVector direction, float tMin, float tMax)
        {
            RayDesc ray = default;
            ray.TMin = tMin;
            ray.TMax = tMax;
            return ray;
        }
    }
}
