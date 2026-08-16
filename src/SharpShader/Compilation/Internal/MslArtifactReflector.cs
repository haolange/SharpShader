using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal static partial class MslArtifactReflector
    {
        private static readonly HashSet<string> s_OutputAttachmentAttributes =
            new(StringComparer.Ordinal)
            {
                "color",
                "depth",
                "index",
                "invariant",
                "stencil",
            };

        private static readonly HashSet<string> s_InputAttachmentAttributes =
            new(StringComparer.Ordinal)
            {
                "center_no_perspective",
                "center_perspective",
                "centroid_no_perspective",
                "centroid_perspective",
                "color",
                "flat",
                "sample_no_perspective",
                "sample_perspective",
            };

        private static readonly HashSet<string> s_RasterOrderTextureAttributes =
            new(StringComparer.Ordinal)
            {
                "raster_order_group",
                "texture",
            };

        private static readonly HashSet<string> s_RasterOrderBufferAttributes =
            new(StringComparer.Ordinal)
            {
                "buffer",
                "raster_order_group",
            };

        private static readonly HashSet<string>
            s_RasterOrderArgumentBufferAttributes =
                new(StringComparer.Ordinal)
                {
                    "id",
                    "raster_order_group",
                };

        private static readonly HashSet<string> s_MslTypeQualifiers =
            new(StringComparer.Ordinal)
            {
                "const",
                "constant",
                "device",
                "thread",
                "threadgroup",
                "volatile",
            };

        internal static MslArtifactReflection Reflect(
            string source,
            string entryPoint,
            ShaderExecutionStage stage)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw Failure("MSL reflection requires a non-empty entry-point name.");
            }

            if (stage != ShaderExecutionStage.Pixel)
            {
                throw Failure(
                    $"MSL attachment reflection only supports pixel entries, not {stage}.");
            }

            List<Token> tokens = Lexer.Tokenize(source);
            Parser parser = new(tokens);
            return parser.Reflect(entryPoint, stage);
        }

        private static ShaderCompilerException Failure(string message)
        {
            return new ShaderCompilerException(
                ShaderCompilerErrorCode.MslTranslateFailed,
                message);
        }

        private enum TokenKind : byte
        {
            Identifier,
            Number,
            Literal,
            Symbol,
            AttributeOpen,
            AttributeClose,
            Scope,
        }

        private readonly struct Token
        {
            public TokenKind Kind { get; }
            public string Text { get; }
            public int Line { get; }
            public int Column { get; }

            public Token(
                TokenKind kind,
                string text,
                int line,
                int column)
            {
                Kind = kind;
                Text = text;
                Line = line;
                Column = column;
            }

            public string Position => $"line {Line}, column {Column}";
        }

        private sealed class ParsedAttribute
        {
            public string Name { get; }
            public IReadOnlyList<Token> Arguments { get; }
            public Token SourceToken { get; }

            public ParsedAttribute(
                string name,
                IReadOnlyList<Token> arguments,
                Token sourceToken)
            {
                Name = name;
                Arguments = arguments;
                SourceToken = sourceToken;
            }
        }

        private sealed class ParsedDeclaration
        {
            public IReadOnlyList<Token> TypeTokens { get; }
            public IReadOnlyList<ParsedAttribute> Attributes { get; }

            public ParsedDeclaration(
                IReadOnlyList<Token> typeTokens,
                IReadOnlyList<ParsedAttribute> attributes)
            {
                TypeTokens = typeTokens;
                Attributes = attributes;
            }
        }

        private sealed class ParsedStruct
        {
            public string Name { get; }
            public IReadOnlyList<ParsedDeclaration> Members { get; }

            public ParsedStruct(
                string name,
                IReadOnlyList<ParsedDeclaration> members)
            {
                Name = name;
                Members = members;
            }
        }

        private sealed class ParsedFunction
        {
            public string Name { get; }
            public ShaderExecutionStage Stage { get; }
            public IReadOnlyList<Token> ReturnTypeTokens { get; }
            public IReadOnlyList<ParsedAttribute> ReturnAttributes { get; }
            public IReadOnlyList<ParsedDeclaration> Parameters { get; }

            public ParsedFunction(
                string name,
                ShaderExecutionStage stage,
                IReadOnlyList<Token> returnTypeTokens,
                IReadOnlyList<ParsedAttribute> returnAttributes,
                IReadOnlyList<ParsedDeclaration> parameters)
            {
                Name = name;
                Stage = stage;
                ReturnTypeTokens = returnTypeTokens;
                ReturnAttributes = returnAttributes;
                Parameters = parameters;
            }
        }

        private readonly struct ValueShape
        {
            public ShaderAttachmentNumericClass NumericClass { get; }
            public uint ComponentCount { get; }

            public ValueShape(
                ShaderAttachmentNumericClass numericClass,
                uint componentCount)
            {
                NumericClass = numericClass;
                ComponentCount = componentCount;
            }
        }

        private readonly struct TextureShape
        {
            public ShaderAttachmentNumericClass NumericClass { get; }
            public ShaderAttachmentSampleMode SampleMode { get; }
            public ShaderAttachmentLayerMode LayerMode { get; }
            public bool IsReadWrite { get; }

            public TextureShape(
                ShaderAttachmentNumericClass numericClass,
                ShaderAttachmentSampleMode sampleMode,
                ShaderAttachmentLayerMode layerMode,
                bool isReadWrite)
            {
                NumericClass = numericClass;
                SampleMode = sampleMode;
                LayerMode = layerMode;
                IsReadWrite = isReadWrite;
            }
        }


        private static bool TryParseStage(
            string token,
            out ShaderExecutionStage stage)
        {
            switch (token)
            {
                case "fragment":
                    stage = ShaderExecutionStage.Pixel;
                    return true;
                case "vertex":
                    stage = ShaderExecutionStage.Vertex;
                    return true;
                case "kernel":
                    stage = ShaderExecutionStage.Compute;
                    return true;
                case "object":
                    stage = ShaderExecutionStage.Amplification;
                    return true;
                case "mesh":
                    stage = ShaderExecutionStage.Mesh;
                    return true;
                default:
                    stage = default;
                    return false;
            }
        }

        private static bool ContainsAttachmentAttribute(
            IReadOnlyList<ParsedAttribute> attributes)
        {
            return FindAttribute(attributes, "color") is not null
                || FindAttribute(attributes, "index") is not null
                || FindAttribute(attributes, "depth") is not null
                || FindAttribute(attributes, "stencil") is not null
                || FindAttribute(attributes, "texture") is not null
                || FindAttribute(attributes, "raster_order_group") is not null;
        }

        private static bool HasAttribute(
            IReadOnlyList<ParsedAttribute> attributes,
            string name)
        {
            return FindAttribute(attributes, name) is not null;
        }

        private static ParsedAttribute? FindAttribute(
            IReadOnlyList<ParsedAttribute> attributes,
            string name)
        {
            foreach (ParsedAttribute attribute in attributes)
            {
                if (string.Equals(
                        attribute.Name,
                        name,
                        StringComparison.Ordinal))
                {
                    return attribute;
                }
            }

            return null;
        }

        private static void ValidateAttributeSet(
            IReadOnlyList<ParsedAttribute> attributes,
            IReadOnlySet<string> allowedAttributes,
            string declarationKind)
        {
            foreach (ParsedAttribute attribute in attributes)
            {
                if (!allowedAttributes.Contains(attribute.Name))
                {
                    throw Failure(
                        $"MSL {declarationKind} declaration contains unknown or "
                        + $"conflicting attribute {attribute.Name} at "
                        + $"{attribute.SourceToken.Position}.");
                }
            }
        }

        private static uint ReadUInt32(ParsedAttribute attribute)
        {
            if (attribute.Arguments.Count != 1
                || attribute.Arguments[0].Kind != TokenKind.Number)
            {
                throw Failure(
                    $"MSL attribute {attribute.Name} at "
                    + $"{attribute.SourceToken.Position} requires one UInt32 "
                    + "literal argument.");
            }

            string text = attribute.Arguments[0].Text;
            while (text.Length != 0
                   && text[^1] is 'u' or 'U' or 'l' or 'L')
            {
                text = text[..^1];
            }

            try
            {
                return text.StartsWith("0x", StringComparison.Ordinal)
                    ? Convert.ToUInt32(text[2..], 16)
                    : uint.Parse(
                        text,
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception exception)
                when (exception is FormatException or OverflowException)
            {
                throw Failure(
                    $"MSL attribute {attribute.Name} at "
                    + $"{attribute.SourceToken.Position} has invalid UInt32 "
                    + $"argument {attribute.Arguments[0].Text}.");
            }
        }

        private static ShaderDepthExport ReadDepthExport(
            ParsedAttribute attribute)
        {
            if (attribute.Arguments.Count != 1
                || attribute.Arguments[0].Kind != TokenKind.Identifier)
            {
                throw Failure(
                    $"MSL depth attribute at {attribute.SourceToken.Position} "
                    + "requires one qualifier.");
            }

            return attribute.Arguments[0].Text switch
            {
                "any" => ShaderDepthExport.Depth,
                "greater" => ShaderDepthExport.DepthGreaterEqual,
                "less" => ShaderDepthExport.DepthLessEqual,
                _ => throw Failure(
                    $"MSL depth attribute at {attribute.SourceToken.Position} "
                    + $"has unknown qualifier {attribute.Arguments[0].Text}."),
            };
        }

        private static void EnsureNoArguments(ParsedAttribute attribute)
        {
            if (attribute.Arguments.Count != 0)
            {
                throw Failure(
                    $"MSL attribute {attribute.Name} at "
                    + $"{attribute.SourceToken.Position} does not accept "
                    + "arguments.");
            }
        }

        private static ValueShape ParseValueShape(
            IReadOnlyList<Token> typeTokens)
        {
            int valueTypeIndex = 0;
            while (valueTypeIndex < typeTokens.Count
                   && typeTokens[valueTypeIndex].Kind == TokenKind.Identifier
                   && s_MslTypeQualifiers.Contains(
                       typeTokens[valueTypeIndex].Text))
            {
                ++valueTypeIndex;
            }

            if (valueTypeIndex >= typeTokens.Count
                || typeTokens[valueTypeIndex].Kind != TokenKind.Identifier
                || !TryParseNumericType(
                    typeTokens[valueTypeIndex].Text,
                    out ShaderAttachmentNumericClass numericClass,
                    out uint componentCount)
                || valueTypeIndex + 1 != typeTokens.Count)
            {
                throw Failure(
                    "MSL attachment declaration must use an exact supported "
                    + "scalar/vector root value type after known qualifiers.");
            }

            return new ValueShape(numericClass, componentCount);
        }

        private static TextureShape ParseTextureShape(
            IReadOnlyList<Token> typeTokens)
        {
            int textureTypeIndex = 0;
            while (textureTypeIndex < typeTokens.Count
                   && typeTokens[textureTypeIndex].Kind == TokenKind.Identifier
                   && s_MslTypeQualifiers.Contains(
                       typeTokens[textureTypeIndex].Text))
            {
                ++textureTypeIndex;
            }

            if (textureTypeIndex >= typeTokens.Count
                || typeTokens[textureTypeIndex].Kind != TokenKind.Identifier)
            {
                throw Failure(
                    "MSL texture binding attribute is attached to a non-texture "
                    + "declaration.");
            }

            string textureType = typeTokens[textureTypeIndex].Text;
            ShaderAttachmentSampleMode sampleMode = textureType switch
            {
                "texture2d" or "texture2d_array" =>
                    ShaderAttachmentSampleMode.SingleSample,
                "texture2d_ms" or "texture2d_ms_array" =>
                    ShaderAttachmentSampleMode.Multisampled,
                _ => throw Failure(
                    $"MSL raster attachment texture type {textureType} is not supported."),
            };
            ShaderAttachmentLayerMode layerMode = textureType switch
            {
                "texture2d" or "texture2d_ms" =>
                    ShaderAttachmentLayerMode.SingleLayer,
                "texture2d_array" or "texture2d_ms_array" =>
                    ShaderAttachmentLayerMode.Layered,
                _ => throw Failure(
                    $"MSL raster attachment texture type {textureType} is not supported."),
            };
            int templateOpen = textureTypeIndex + 1;
            if (templateOpen >= typeTokens.Count
                || typeTokens[templateOpen].Text != "<")
            {
                throw Failure(
                    $"MSL texture type {textureType} has no element type.");
            }

            ShaderAttachmentNumericClass? numericClass = null;
            for (int index = templateOpen + 1;
                 index < typeTokens.Count && typeTokens[index].Text != ">";
                 ++index)
            {
                if (typeTokens[index].Kind == TokenKind.Identifier
                    && TryParseNumericType(
                        typeTokens[index].Text,
                        out ShaderAttachmentNumericClass parsedNumericClass,
                        out _))
                {
                    numericClass = parsedNumericClass;
                    break;
                }
            }

            if (!numericClass.HasValue)
            {
                throw Failure(
                    $"MSL texture type {textureType} has no supported numeric "
                    + "element type.");
            }

            string? accessMode = null;
            for (int index = templateOpen + 1;
                 index + 2 < typeTokens.Count;
                 ++index)
            {
                if (typeTokens[index].Text != "access")
                {
                    continue;
                }

                if (accessMode is not null
                    || index + 2 >= typeTokens.Count
                    || typeTokens[index + 1].Kind != TokenKind.Scope
                    || typeTokens[index + 2].Kind != TokenKind.Identifier)
                {
                    throw Failure(
                        $"MSL texture type {textureType} has malformed or "
                        + "duplicate access qualification.");
                }

                accessMode = typeTokens[index + 2].Text;
            }

            if (accessMode is not null
                && accessMode is not ("read" or "write" or "read_write" or "sample"))
            {
                throw Failure(
                    $"MSL texture type {textureType} has unknown access mode "
                    + $"{accessMode}.");
            }

            bool isReadWrite = string.Equals(
                accessMode,
                "read_write",
                StringComparison.Ordinal);
            return new TextureShape(
                numericClass.Value,
                sampleMode,
                layerMode,
                isReadWrite);
        }

        private static bool TryParseNumericType(
            string type,
            out ShaderAttachmentNumericClass numericClass,
            out uint componentCount)
        {
            string candidate = type.StartsWith(
                "packed_",
                StringComparison.Ordinal)
                ? type["packed_".Length..]
                : type;
            componentCount = 1;
            if (candidate.Length > 1
                && candidate[^1] is >= '2' and <= '4')
            {
                componentCount = checked((uint)(candidate[^1] - '0'));
                candidate = candidate[..^1];
            }

            if (candidate is "float" or "half" or "double")
            {
                numericClass = ShaderAttachmentNumericClass.FloatingPoint;
                return true;
            }

            if (candidate is "int"
                or "short"
                or "long"
                or "char"
                or "int8_t"
                or "int16_t"
                or "int32_t"
                or "int64_t")
            {
                numericClass = ShaderAttachmentNumericClass.SignedInteger;
                return true;
            }

            if (candidate is "uint"
                or "ushort"
                or "ulong"
                or "uchar"
                or "uint8_t"
                or "uint16_t"
                or "uint32_t"
                or "uint64_t")
            {
                numericClass = ShaderAttachmentNumericClass.UnsignedInteger;
                return true;
            }

            numericClass = default;
            componentCount = 0;
            return false;
        }

        private static void ValidateUniqueColorIo(
            IReadOnlyList<MslColorAttachmentIoReflection> values,
            string direction)
        {
            HashSet<(uint Location, uint Index)> keys = new();
            foreach (MslColorAttachmentIoReflection value in values)
            {
                if (!keys.Add((value.Location, value.Index)))
                {
                    throw Failure(
                        $"MSL contains duplicate color {direction} at "
                        + $"location/index {value.Location}/{value.Index}.");
                }
            }
        }

        private static void ValidateUniqueTextures(
            IReadOnlyList<MslTextureBindingReflection> textures)
        {
            HashSet<uint> indices = new();
            foreach (MslTextureBindingReflection texture in textures)
            {
                if (!indices.Add(texture.TextureIndex))
                {
                    throw Failure(
                        $"MSL contains duplicate texture binding "
                        + $"{texture.TextureIndex}.");
                }
            }
        }

        private static void ValidateUniqueRasterOrderGroups(
            IReadOnlyList<MslRasterOrderGroupReflection> values)
        {
            HashSet<(MslResourceBindingKind Kind, uint Index)> keys = new();
            foreach (MslRasterOrderGroupReflection value in values)
            {
                if (!keys.Add((value.BindingKind, value.BindingIndex)))
                {
                    throw Failure(
                        $"MSL contains duplicate raster-order binding "
                        + $"{value.BindingKind}/{value.BindingIndex}.");
                }
            }
        }

    }
}
