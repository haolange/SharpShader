namespace SharpShader.CSharp.Frontend
{
    public static class CSharpShaderDiagnosticIds
    {
        public const string Prefix = "SSCS";
        public const string NoEntry = "SSCS0001";
        public const string UnsupportedSyntax = "SSCS0002";
        public const string UnknownType = "SSCS0003";
        public const string UnknownIntrinsic = "SSCS0004";
        public const string BoolVectorInLayout = "SSCS0005";
        public const string LayoutMismatch = "SSCS0006";
        public const string RayQueryMetalUnsupported = "SSCS0007";
        public const string MissingStage = "SSCS0008";
        public const string CSharpError = "SSCS0009";
        public const string Recursion = "SSCS0010";
        public const string InvalidResource = "SSCS0011";
        public const string InvalidCapture = "SSCS0012";
    }
}
