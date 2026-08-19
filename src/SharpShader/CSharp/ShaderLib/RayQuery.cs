namespace SharpShader.CSharp.ShaderLib
{
    public struct RayQuery
    {
        public RayQuery(RayQueryFlags flags)
        {
            Flags = flags;
        }

        public RayQueryFlags Flags { get; }

        [Intrinsic("RAY_QUERY_TRACE_RAY_INLINE")]
        public void TraceRayInline(RaytracingAccelerationStructure accelerationStructure, uint mask, RayDesc ray)
        {
        }

        [Intrinsic("RAY_QUERY_PROCEED")]
        public bool Proceed()
        {
            return false;
        }

        [Intrinsic("RAY_QUERY_TERMINATE")]
        public void Abort()
        {
        }

        [Intrinsic("RAY_QUERY_COMMIT_TRIANGLE")]
        public void CommitNonOpaqueTriangleHit()
        {
        }

        [Intrinsic("RAY_QUERY_COMMITTED_STATUS")]
        public HitStatus CommittedStatus()
        {
            return HitStatus.Miss;
        }

        [Intrinsic("RAY_QUERY_COMMITTED_TRIANGLE_BARYCENTRICS")]
        public TVector CommittedTriangleBarycentrics<TVector>()
        {
            return default!;
        }

        [Intrinsic("RAY_QUERY_COMMITTED_PRIMITIVE_INDEX")]
        public uint CommittedPrimitiveIndex()
        {
            return 0;
        }
    }
}
