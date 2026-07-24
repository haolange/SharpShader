using System.Text.Json.Serialization;

namespace SharpShader.Compilation.Internal
{
    internal sealed class ShaderInterfaceManifestDocument
    {
        [JsonPropertyOrder(0)]
        public uint? SchemaVersion { get; set; }

        [JsonPropertyOrder(1)]
        public string? SourceDigest { get; set; }

        [JsonPropertyOrder(2)]
        public ShaderToolchainComponentDocument[]? Toolchain { get; set; }

        [JsonPropertyOrder(3)]
        public ShaderInterfaceLayoutDocument[]? Layouts { get; set; }

        [JsonPropertyOrder(4)]
        public ShaderInterfaceVariantDocument[]? Variants { get; set; }

        [JsonPropertyOrder(5)]
        public ShaderBackendLayoutsDocument[]? BackendLayouts { get; set; }
    }

    internal sealed class ShaderToolchainComponentDocument
    {
        [JsonPropertyOrder(0)]
        public string? Name { get; set; }

        [JsonPropertyOrder(1)]
        public string? Version { get; set; }

        [JsonPropertyOrder(2)]
        public string? ContentDigest { get; set; }
    }

    internal sealed class ShaderInterfaceLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public string? Signature { get; set; }

        [JsonPropertyOrder(1)]
        public ShaderLogicalBindingDocument[]? Bindings { get; set; }
    }

    internal sealed class ShaderLogicalBindingDocument
    {
        [JsonPropertyOrder(0)]
        public ShaderBindingKeyDocument? Key { get; set; }

        [JsonPropertyOrder(1)]
        public string? CanonicalName { get; set; }

        [JsonPropertyOrder(2)]
        public string[]? Aliases { get; set; }

        [JsonPropertyOrder(3)]
        public ShaderResourceShapeDocument? Shape { get; set; }

        [JsonPropertyOrder(4)]
        public string[]? Stages { get; set; }

        [JsonPropertyOrder(5)]
        public string? Provenance { get; set; }

        [JsonPropertyOrder(6)]
        public ShaderConstantBufferLayoutDocument? ConstantBuffer { get; set; }
    }

    internal sealed class ShaderBindingKeyDocument
    {
        [JsonPropertyOrder(0)]
        public uint? Table { get; set; }

        [JsonPropertyOrder(1)]
        public uint? Slot { get; set; }

        [JsonPropertyOrder(2)]
        public string? Type { get; set; }
    }

    internal sealed class ShaderResourceShapeDocument
    {
        [JsonPropertyOrder(0)]
        public string? Kind { get; set; }

        [JsonPropertyOrder(1)]
        public string? Dimension { get; set; }

        [JsonPropertyOrder(2)]
        public string? Access { get; set; }

        [JsonPropertyOrder(3)]
        public ShaderArrayExtentDocument[]? Array { get; set; }

        [JsonPropertyOrder(4)]
        public uint? StructureStride { get; set; }

        [JsonPropertyOrder(5)]
        public string? SamplerKind { get; set; }

        [JsonPropertyOrder(6)]
        public string? CounterKind { get; set; }
    }

    internal sealed class ShaderArrayExtentDocument
    {
        [JsonPropertyOrder(0)]
        public string? Kind { get; set; }

        [JsonPropertyOrder(1)]
        public uint? Value { get; set; }
    }

    internal sealed class ShaderConstantBufferLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public string? Name { get; set; }

        [JsonPropertyOrder(1)]
        public uint? ByteSize { get; set; }

        [JsonPropertyOrder(2)]
        public ShaderValueMemberDocument[]? Variables { get; set; }
    }

    internal sealed class ShaderValueMemberDocument
    {
        [JsonPropertyOrder(0)]
        public string? Name { get; set; }

        [JsonPropertyOrder(1)]
        public uint? ByteOffset { get; set; }

        [JsonPropertyOrder(2)]
        public uint? ByteSize { get; set; }

        [JsonPropertyOrder(3)]
        public ShaderValueLayoutDocument? Value { get; set; }
    }

    internal sealed class ShaderValueLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public string? Kind { get; set; }

        [JsonPropertyOrder(1)]
        public string? ScalarType { get; set; }

        [JsonPropertyOrder(2)]
        public uint? Rows { get; set; }

        [JsonPropertyOrder(3)]
        public uint? Columns { get; set; }

        [JsonPropertyOrder(4)]
        public ShaderArrayExtentDocument[]? Array { get; set; }

        [JsonPropertyOrder(5)]
        public uint? ByteSize { get; set; }

        [JsonPropertyOrder(6)]
        public uint? ProvenArrayStride { get; set; }

        [JsonPropertyOrder(7)]
        public uint? ProvenMatrixStride { get; set; }

        [JsonPropertyOrder(8)]
        public string? MatrixMajorOrder { get; set; }

        [JsonPropertyOrder(9)]
        public ShaderValueMemberDocument[]? Members { get; set; }
    }

    internal sealed class ShaderInterfaceVariantDocument
    {
        [JsonPropertyOrder(0)]
        public string? Key { get; set; }

        [JsonPropertyOrder(1)]
        public string[]? Defines { get; set; }

        [JsonPropertyOrder(2)]
        public ShaderInterfaceEntryDocument[]? Entries { get; set; }
    }

    internal sealed class ShaderInterfaceEntryDocument
    {
        [JsonPropertyOrder(0)]
        public string? Name { get; set; }

        [JsonPropertyOrder(1)]
        public string? Stage { get; set; }

        [JsonPropertyOrder(2)]
        public string? LogicalLayoutSignature { get; set; }

        [JsonPropertyOrder(3)]
        public ShaderArtifactIdentityDocument[]? Artifacts { get; set; }
    }

    internal sealed class ShaderArtifactIdentityDocument
    {
        [JsonPropertyOrder(0)]
        public string? ArtifactKind { get; set; }

        [JsonPropertyOrder(1)]
        public string? ContentDigest { get; set; }

        [JsonPropertyOrder(2)]
        public ulong? ByteLength { get; set; }

        [JsonPropertyOrder(3)]
        public string? ArtifactName { get; set; }
    }

    internal sealed class ShaderBackendLayoutsDocument
    {
        [JsonPropertyOrder(0)]
        public string? LogicalLayoutSignature { get; set; }

        [JsonPropertyOrder(1)]
        public Dx12ShaderBackendLayoutDocument? Dx12 { get; set; }

        [JsonPropertyOrder(2)]
        public VulkanShaderBackendLayoutDocument? Vulkan { get; set; }

        [JsonPropertyOrder(3)]
        public MetalShaderBackendLayoutDocument? Metal { get; set; }
    }

    internal sealed class Dx12ShaderBackendLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public Dx12ShaderBindingMappingDocument[]? Bindings { get; set; }
    }

    internal sealed class Dx12ShaderBindingMappingDocument
    {
        [JsonPropertyOrder(0)]
        public ShaderBindingKeyDocument? LogicalBinding { get; set; }

        [JsonPropertyOrder(1)]
        public uint? RegisterSpace { get; set; }

        [JsonPropertyOrder(2)]
        public uint? ShaderRegister { get; set; }

        [JsonPropertyOrder(3)]
        public string? RegisterClass { get; set; }
    }

    internal sealed class VulkanShaderBackendLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public VulkanShaderBindingMappingDocument[]? Bindings { get; set; }
    }

    internal sealed class VulkanShaderBindingMappingDocument
    {
        [JsonPropertyOrder(0)]
        public ShaderBindingKeyDocument? LogicalBinding { get; set; }

        [JsonPropertyOrder(1)]
        public uint? DescriptorSet { get; set; }

        [JsonPropertyOrder(2)]
        public uint? Binding { get; set; }

        [JsonPropertyOrder(3)]
        public string? DescriptorKind { get; set; }
    }

    internal sealed class MetalShaderBackendLayoutDocument
    {
        [JsonPropertyOrder(0)]
        public MetalDirectBindingMappingDocument[]? DirectBindings { get; set; }

        [JsonPropertyOrder(1)]
        public MetalReferenceBufferBindingMappingDocument[]? ReferenceBufferBindings { get; set; }
    }

    internal sealed class MetalDirectBindingMappingDocument
    {
        [JsonPropertyOrder(0)]
        public ShaderBindingKeyDocument? LogicalBinding { get; set; }

        [JsonPropertyOrder(1)]
        public uint? ArgumentTable { get; set; }

        [JsonPropertyOrder(2)]
        public string? Namespace { get; set; }

        [JsonPropertyOrder(3)]
        public uint? Index { get; set; }
    }

    internal sealed class MetalReferenceBufferBindingMappingDocument
    {
        [JsonPropertyOrder(0)]
        public ShaderBindingKeyDocument? LogicalBinding { get; set; }

        [JsonPropertyOrder(1)]
        public uint? ArgumentTable { get; set; }

        [JsonPropertyOrder(2)]
        public string? ResourceNamespace { get; set; }

        [JsonPropertyOrder(3)]
        public uint? ReferenceBufferIndex { get; set; }

        [JsonPropertyOrder(4)]
        public ulong? ByteOffset { get; set; }

        [JsonPropertyOrder(5)]
        public uint? ReferenceCount { get; set; }
    }

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        GenerationMode = JsonSourceGenerationMode.Metadata,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
    [JsonSerializable(typeof(ShaderInterfaceManifestDocument))]
    internal sealed partial class ShaderInterfaceManifestJsonContext : JsonSerializerContext
    {
    }
}
