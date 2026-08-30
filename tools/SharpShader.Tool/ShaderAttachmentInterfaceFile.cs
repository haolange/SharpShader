using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpShader.Compilation;

namespace SharpShader.Tool
{
    internal static class ShaderAttachmentInterfaceFile
    {
        internal const uint CurrentSchemaRevision = 2;
        private const long MaximumFileBytes = 16L * 1024 * 1024;
        private static readonly UTF8Encoding s_StrictUtf8 = new(false, true);
        private static readonly JsonSerializerOptions s_JsonOptions =
            CreateJsonOptions();

        public static IReadOnlyList<ShaderAttachmentInterface> Load(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException(
                    "Attachment-interface path must not be empty.",
                    nameof(path));
            }

            string fullPath = Path.GetFullPath(path);
            string json = ReadUtf8TextBounded(fullPath);
            using JsonDocument syntax = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64,
                });
            ValidateUniqueProperties(syntax.RootElement, "$", depth: 0);

            AttachmentInterfaceFileDocument document;
            try
            {
                document = JsonSerializer.Deserialize<AttachmentInterfaceFileDocument>(
                    json,
                    s_JsonOptions)
                    ?? throw Invalid("Attachment interface document is null.");
            }
            catch (JsonException exception)
            {
                throw Invalid(
                    "Attachment interface JSON does not match the strict schema.",
                    exception);
            }

            if (document.SchemaRevision != CurrentSchemaRevision)
            {
                throw Invalid(
                    $"Attachment interface schema revision must be "
                    + $"{CurrentSchemaRevision}, not "
                    + $"{document.SchemaRevision?.ToString(CultureInfo.InvariantCulture) ?? "missing"}.");
            }

            if (document.Interfaces is null)
            {
                throw Invalid(
                    "Attachment interface document requires an interfaces array.");
            }

            ShaderAttachmentInterface[] result =
                new ShaderAttachmentInterface[document.Interfaces.Count];
            for (int index = 0; index < result.Length; ++index)
            {
                AttachmentInterfaceDocument item = document.Interfaces[index]
                    ?? throw Invalid($"interfaces[{index}] is null.");
                result[index] = Materialize(item, $"interfaces[{index}]");
            }

            return Array.AsReadOnly(result);
        }

        private static ShaderAttachmentInterface Materialize(
            AttachmentInterfaceDocument document,
            string path)
        {
            uint abiRevision = Require(document.AbiRevision, $"{path}.abiRevision");
            string variantKey = Require(document.VariantKey, $"{path}.variantKey");
            string entryPoint = Require(document.EntryPoint, $"{path}.entryPoint");
            ShaderExecutionStage stage = Require(document.Stage, $"{path}.stage");
            if (!document.HasPhase)
            {
                throw Invalid($"{path}.phase is required (use null when absent).");
            }

            ShaderAttachmentPhase? phase = document.Phase is null
                ? null
                : Materialize(document.Phase, $"{path}.phase");
            try
            {
                return new ShaderAttachmentInterface(
                    variantKey,
                    entryPoint,
                    stage,
                    phase,
                    abiRevision);
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"{path} is invalid: {exception.Message}", exception);
            }
        }

        private static ShaderAttachmentPhase Materialize(
            AttachmentPhaseDocument document,
            string path)
        {
            uint phase = Require(document.Phase, $"{path}.phase");
            ShaderDepthStencilAccess depthStencilAccess = Require(
                document.DepthStencilAccess,
                $"{path}.depthStencilAccess");
            ShaderDepthExport depthExport = Require(
                document.DepthExport,
                $"{path}.depthExport");
            ShaderStencilExport stencilExport = Require(
                document.StencilExport,
                $"{path}.stencilExport");
            if (document.Attachments is null)
            {
                throw Invalid($"{path}.attachments is required.");
            }

            ShaderAttachmentDeclaration[] attachments =
                new ShaderAttachmentDeclaration[document.Attachments.Count];
            for (int index = 0; index < attachments.Length; ++index)
            {
                AttachmentDeclarationDocument item = document.Attachments[index]
                    ?? throw Invalid($"{path}.attachments[{index}] is null.");
                attachments[index] = Materialize(
                    item,
                    $"{path}.attachments[{index}]");
            }

            try
            {
                return new ShaderAttachmentPhase(
                    phase,
                    attachments,
                    depthStencilAccess,
                    depthExport,
                    stencilExport);
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"{path} is invalid: {exception.Message}", exception);
            }
        }

        private static ShaderAttachmentDeclaration Materialize(
            AttachmentDeclarationDocument document,
            string path)
        {
            if (!document.HasInputIndex)
            {
                throw Invalid($"{path}.inputIndex is required (use null when absent).");
            }

            if (!document.HasOutputLocation)
            {
                throw Invalid(
                    $"{path}.outputLocation is required (use null when absent).");
            }

            try
            {
                return new ShaderAttachmentDeclaration(
                    Require(document.LogicalAttachmentId, $"{path}.logicalAttachmentId"),
                    document.InputIndex,
                    document.OutputLocation,
                    Require(document.Aspect, $"{path}.aspect"),
                    Require(document.NumericClass, $"{path}.numericClass"),
                    Require(document.SampleMode, $"{path}.sampleMode"),
                    Require(document.LayerMode, $"{path}.layerMode"),
                    Require(document.OutputIndex, $"{path}.outputIndex"),
                    Require(document.OutputComponent, $"{path}.outputComponent"));
            }
            catch (ArgumentException exception)
            {
                throw Invalid($"{path} is invalid: {exception.Message}", exception);
            }
        }

        private static T Require<T>(T? value, string path)
            where T : struct
        {
            return value ?? throw Invalid($"{path} is required.");
        }

        private static string Require(string? value, string path)
        {
            return !string.IsNullOrWhiteSpace(value)
                ? value
                : throw Invalid($"{path} is required and must not be empty.");
        }

        private static void ValidateUniqueProperties(
            JsonElement element,
            string path,
            int depth)
        {
            if (depth > 64)
            {
                throw Invalid("Attachment interface JSON exceeds depth 64.");
            }

            if (element.ValueKind == JsonValueKind.Object)
            {
                HashSet<string> names = new(StringComparer.Ordinal);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw Invalid(
                            $"Attachment interface JSON contains duplicate property "
                            + $"{path}.{property.Name}.");
                    }

                    ValidateUniqueProperties(
                        property.Value,
                        $"{path}.{property.Name}",
                        depth + 1);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in element.EnumerateArray())
                {
                    ValidateUniqueProperties(
                        item,
                        $"{path}[{index}]",
                        depth + 1);
                    ++index;
                }
            }
        }

        private static string ReadUtf8TextBounded(string path)
        {
            FileInfo file = new(path);
            if (!file.Exists)
            {
                throw new FileNotFoundException(
                    "Attachment interface file was not found.",
                    path);
            }

            if (file.Length > MaximumFileBytes || file.Length > int.MaxValue)
            {
                throw new IOException(
                    $"Attachment interface file {path} contains {file.Length} "
                    + $"bytes, exceeding the {MaximumFileBytes}-byte limit.");
            }

            byte[] bytes = new byte[checked((int)file.Length)];
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.SequentialScan);
            stream.ReadExactly(bytes);
            if (stream.ReadByte() != -1)
            {
                throw new IOException(
                    $"Attachment interface file {path} changed while being read.");
            }

            try
            {
                string value = s_StrictUtf8.GetString(bytes);
                return value.Length > 0 && value[0] == '\uFEFF'
                    ? value[1..]
                    : value;
            }
            catch (DecoderFallbackException exception)
            {
                throw Invalid(
                    $"Attachment interface file {path} is not valid UTF-8.",
                    exception);
            }
        }

        private static JsonSerializerOptions CreateJsonOptions()
        {
            JsonSerializerOptions options = new()
            {
                PropertyNameCaseInsensitive = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            };
            options.Converters.Add(
                new JsonStringEnumConverter(
                    JsonNamingPolicy.CamelCase,
                    allowIntegerValues: false));
            return options;
        }

        private static InvalidDataException Invalid(
            string message,
            Exception? innerException = null)
        {
            return new InvalidDataException(message, innerException);
        }

        private sealed class AttachmentInterfaceFileDocument
        {
            public uint? SchemaRevision { get; set; }
            public List<AttachmentInterfaceDocument?>? Interfaces { get; set; }
        }

        private sealed class AttachmentInterfaceDocument
        {
            private AttachmentPhaseDocument? m_Phase;

            public uint? AbiRevision { get; set; }
            public string? VariantKey { get; set; }
            public string? EntryPoint { get; set; }
            public ShaderExecutionStage? Stage { get; set; }
            [JsonIgnore]
            public bool HasPhase { get; private set; }
            public AttachmentPhaseDocument? Phase
            {
                get => m_Phase;
                set
                {
                    HasPhase = true;
                    m_Phase = value;
                }
            }
        }

        private sealed class AttachmentPhaseDocument
        {
            public uint? Phase { get; set; }
            public ShaderDepthStencilAccess? DepthStencilAccess { get; set; }
            public ShaderDepthExport? DepthExport { get; set; }
            public ShaderStencilExport? StencilExport { get; set; }
            public List<AttachmentDeclarationDocument?>? Attachments { get; set; }
        }

        private sealed class AttachmentDeclarationDocument
        {
            private uint? m_InputIndex;
            private uint? m_OutputLocation;

            public uint? LogicalAttachmentId { get; set; }
            [JsonIgnore]
            public bool HasInputIndex { get; private set; }
            public uint? InputIndex
            {
                get => m_InputIndex;
                set
                {
                    HasInputIndex = true;
                    m_InputIndex = value;
                }
            }
            [JsonIgnore]
            public bool HasOutputLocation { get; private set; }
            public uint? OutputLocation
            {
                get => m_OutputLocation;
                set
                {
                    HasOutputLocation = true;
                    m_OutputLocation = value;
                }
            }
            public uint? OutputIndex { get; set; }
            public uint? OutputComponent { get; set; }
            public ShaderAttachmentAspect? Aspect { get; set; }
            public ShaderAttachmentNumericClass? NumericClass { get; set; }
            public ShaderAttachmentSampleMode? SampleMode { get; set; }
            public ShaderAttachmentLayerMode? LayerMode { get; set; }
        }
    }
}
