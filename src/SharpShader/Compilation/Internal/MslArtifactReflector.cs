using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{
    internal enum MslResourceBindingKind : byte
    {
        Texture,
        Buffer,
        ArgumentBufferId,
    }

    internal sealed class MslColorAttachmentIoReflection
    {
        public ShaderStageIoDirection Direction { get; }
        public uint Location { get; }
        public uint Index { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public uint ComponentCount { get; }

        public MslColorAttachmentIoReflection(
            ShaderStageIoDirection direction,
            uint location,
            uint index,
            ShaderAttachmentNumericClass numericClass,
            uint componentCount)
        {
            if (!Enum.IsDefined(direction))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(direction),
                    direction,
                    "MSL attachment I/O direction is not defined.");
            }

            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "MSL color attachment I/O requires a color numeric class.");
            }

            if (componentCount is < 1 or > 4)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(componentCount),
                    componentCount,
                    "MSL color attachment component count must be in [1, 4].");
            }

            Direction = direction;
            Location = location;
            Index = index;
            NumericClass = numericClass;
            ComponentCount = componentCount;
        }
    }

    internal sealed class MslTextureBindingReflection
    {
        public uint TextureIndex { get; }
        public ShaderAttachmentNumericClass NumericClass { get; }
        public ShaderAttachmentSampleMode SampleMode { get; }
        public ShaderAttachmentLayerMode LayerMode { get; }
        public bool IsReadWrite { get; }
        public uint? RasterOrderGroup { get; }

        public MslTextureBindingReflection(
            uint textureIndex,
            ShaderAttachmentNumericClass numericClass,
            ShaderAttachmentSampleMode sampleMode,
            ShaderAttachmentLayerMode layerMode,
            bool isReadWrite,
            uint? rasterOrderGroup)
        {
            if (!Enum.IsDefined(numericClass)
                || numericClass == ShaderAttachmentNumericClass.DepthStencil)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(numericClass),
                    numericClass,
                    "MSL textures require a color numeric class.");
            }

            if (!Enum.IsDefined(sampleMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(sampleMode),
                    sampleMode,
                    "MSL texture sample mode is not defined.");
            }

            if (!Enum.IsDefined(layerMode))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(layerMode),
                    layerMode,
                    "MSL texture layer mode is not defined.");
            }

            TextureIndex = textureIndex;
            NumericClass = numericClass;
            SampleMode = sampleMode;
            LayerMode = layerMode;
            IsReadWrite = isReadWrite;
            RasterOrderGroup = rasterOrderGroup;
        }
    }

    internal sealed class MslRasterOrderGroupReflection
    {
        public MslResourceBindingKind BindingKind { get; }
        public uint BindingIndex { get; }
        public uint Group { get; }

        public MslRasterOrderGroupReflection(
            MslResourceBindingKind bindingKind,
            uint bindingIndex,
            uint group)
        {
            if (!Enum.IsDefined(bindingKind))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(bindingKind),
                    bindingKind,
                    "MSL raster-order binding kind is not defined.");
            }

            BindingKind = bindingKind;
            BindingIndex = bindingIndex;
            Group = group;
        }
    }

    internal sealed class MslArtifactReflection
    {
        private readonly ReadOnlyCollection<MslColorAttachmentIoReflection>
            m_ColorInputs;
        private readonly ReadOnlyCollection<MslColorAttachmentIoReflection>
            m_ColorOutputs;
        private readonly ReadOnlyCollection<MslTextureBindingReflection>
            m_TextureBindings;
        private readonly ReadOnlyCollection<MslRasterOrderGroupReflection>
            m_RasterOrderGroups;

        public string EntryPoint { get; }
        public ShaderExecutionStage Stage { get; }
        public IReadOnlyList<MslColorAttachmentIoReflection> ColorInputs =>
            m_ColorInputs;
        public IReadOnlyList<MslColorAttachmentIoReflection> ColorOutputs =>
            m_ColorOutputs;
        public IReadOnlyList<MslTextureBindingReflection> TextureBindings =>
            m_TextureBindings;
        public IReadOnlyList<MslRasterOrderGroupReflection> RasterOrderGroups =>
            m_RasterOrderGroups;
        public ShaderDepthExport DepthExport { get; }
        public ShaderStencilExport StencilExport { get; }
        public ShaderAttachmentArtifactRequirement AttachmentRequirements { get; }

        public MslArtifactReflection(
            string entryPoint,
            ShaderExecutionStage stage,
            MslColorAttachmentIoReflection[] colorInputs,
            MslColorAttachmentIoReflection[] colorOutputs,
            MslTextureBindingReflection[] textureBindings,
            MslRasterOrderGroupReflection[] rasterOrderGroups,
            ShaderDepthExport depthExport,
            ShaderStencilExport stencilExport)
        {
            if (string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException(
                    "MSL entry-point name must not be empty.",
                    nameof(entryPoint));
            }

            if (!Enum.IsDefined(stage))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stage),
                    stage,
                    "MSL entry-point stage is not defined.");
            }

            ArgumentNullException.ThrowIfNull(colorInputs);
            ArgumentNullException.ThrowIfNull(colorOutputs);
            ArgumentNullException.ThrowIfNull(textureBindings);
            ArgumentNullException.ThrowIfNull(rasterOrderGroups);
            if (!Enum.IsDefined(depthExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(depthExport),
                    depthExport,
                    "MSL depth export is not defined.");
            }

            if (!Enum.IsDefined(stencilExport))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(stencilExport),
                    stencilExport,
                    "MSL stencil export is not defined.");
            }

            Array.Sort(colorInputs, CompareColorIo);
            Array.Sort(colorOutputs, CompareColorIo);
            Array.Sort(
                textureBindings,
                static (left, right) =>
                    left.TextureIndex.CompareTo(right.TextureIndex));
            Array.Sort(
                rasterOrderGroups,
                static (left, right) =>
                {
                    int kind = left.BindingKind.CompareTo(right.BindingKind);
                    return kind != 0
                        ? kind
                        : left.BindingIndex.CompareTo(right.BindingIndex);
                });

            EntryPoint = entryPoint;
            Stage = stage;
            m_ColorInputs = Array.AsReadOnly(colorInputs);
            m_ColorOutputs = Array.AsReadOnly(colorOutputs);
            m_TextureBindings = Array.AsReadOnly(textureBindings);
            m_RasterOrderGroups = Array.AsReadOnly(rasterOrderGroups);
            DepthExport = depthExport;
            StencilExport = stencilExport;

            ShaderAttachmentArtifactRequirement requirements =
                ShaderAttachmentArtifactRequirement.None;
            if (colorInputs.Length != 0)
            {
                requirements |=
                    ShaderAttachmentArtifactRequirement.FramebufferLocalRead;
            }

            if (rasterOrderGroups.Length != 0)
            {
                requirements |= ShaderAttachmentArtifactRequirement
                    .OrderedPixelFragmentInterlock;
            }

            if (stencilExport == ShaderStencilExport.StencilReference)
            {
                requirements |=
                    ShaderAttachmentArtifactRequirement.StencilReferenceExport;
            }

            AttachmentRequirements = requirements;
        }

        private static int CompareColorIo(
            MslColorAttachmentIoReflection left,
            MslColorAttachmentIoReflection right)
        {
            int location = left.Location.CompareTo(right.Location);
            return location != 0
                ? location
                : left.Index.CompareTo(right.Index);
        }
    }

    internal static class MslArtifactReflector
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

        private sealed class Parser
        {
            private readonly IReadOnlyList<Token> m_Tokens;
            private readonly Dictionary<string, ParsedStruct> m_Structs =
                new(StringComparer.Ordinal);
            private readonly List<ParsedFunction> m_Functions = new();

            public Parser(IReadOnlyList<Token> tokens)
            {
                m_Tokens = tokens;
                ParseTranslationUnit();
            }

            public MslArtifactReflection Reflect(
                string entryPoint,
                ShaderExecutionStage stage)
            {
                ParsedFunction? selected = null;
                foreach (ParsedFunction function in m_Functions)
                {
                    if (!string.Equals(
                            function.Name,
                            entryPoint,
                            StringComparison.Ordinal)
                        || function.Stage != stage)
                    {
                        continue;
                    }

                    if (selected is not null)
                    {
                        throw Failure(
                            $"MSL contains duplicate {stage} entry point "
                            + $"{entryPoint}.");
                    }

                    selected = function;
                }

                if (selected is null)
                {
                    throw Failure(
                        $"MSL does not contain {stage} entry point {entryPoint}.");
                }

                List<MslColorAttachmentIoReflection> colorInputs = new();
                List<MslColorAttachmentIoReflection> colorOutputs = new();
                List<MslTextureBindingReflection> textures = new();
                List<MslRasterOrderGroupReflection> rasterOrderGroups = new();
                ShaderDepthExport depthExport = ShaderDepthExport.None;
                ShaderStencilExport stencilExport = ShaderStencilExport.None;

                ReflectOutputDeclaration(
                    selected.ReturnTypeTokens,
                    selected.ReturnAttributes,
                    colorOutputs,
                    ref depthExport,
                    ref stencilExport);
                string? returnStructName =
                    FindReferencedStruct(selected.ReturnTypeTokens);
                if (returnStructName is not null)
                {
                    foreach (ParsedDeclaration member in
                             m_Structs[returnStructName].Members)
                    {
                        ReflectOutputDeclaration(
                            member.TypeTokens,
                            member.Attributes,
                            colorOutputs,
                            ref depthExport,
                            ref stencilExport);
                    }
                }

                foreach (ParsedDeclaration parameter in selected.Parameters)
                {
                    if (HasAttribute(parameter.Attributes, "stage_in"))
                    {
                        string? inputStructName =
                            FindReferencedStruct(parameter.TypeTokens);
                        if (inputStructName is null)
                        {
                            throw Failure(
                                $"MSL entry {entryPoint} has a stage_in parameter "
                                + "whose type is not a declared struct.");
                        }

                        foreach (ParsedDeclaration member in
                                 m_Structs[inputStructName].Members)
                        {
                            ReflectInputDeclaration(
                                member.TypeTokens,
                                member.Attributes,
                                colorInputs);
                        }
                    }

                    ReflectInputDeclaration(
                        parameter.TypeTokens,
                        parameter.Attributes,
                        colorInputs);
                    ReflectResourceDeclaration(
                        parameter.TypeTokens,
                        parameter.Attributes,
                        textures,
                        rasterOrderGroups);
                }

                ValidateUniqueColorIo(colorInputs, "input");
                ValidateUniqueColorIo(colorOutputs, "output");
                ValidateUniqueTextures(textures);
                ValidateUniqueRasterOrderGroups(rasterOrderGroups);
                return new MslArtifactReflection(
                    entryPoint,
                    stage,
                    colorInputs.ToArray(),
                    colorOutputs.ToArray(),
                    textures.ToArray(),
                    rasterOrderGroups.ToArray(),
                    depthExport,
                    stencilExport);
            }

            private void ParseTranslationUnit()
            {
                int braceDepth = 0;
                for (int index = 0; index < m_Tokens.Count; ++index)
                {
                    Token token = m_Tokens[index];
                    if (token.Text == "{")
                    {
                        ++braceDepth;
                        continue;
                    }

                    if (token.Text == "}")
                    {
                        --braceDepth;
                        if (braceDepth < 0)
                        {
                            throw Failure(
                                $"MSL has an unmatched closing brace at "
                                + $"{token.Position}.");
                        }

                        continue;
                    }

                    if (braceDepth != 0 || token.Kind != TokenKind.Identifier)
                    {
                        continue;
                    }

                    if (token.Text == "struct")
                    {
                        index = ParseStruct(index);
                        continue;
                    }

                    if (TryParseStage(token.Text, out ShaderExecutionStage stage))
                    {
                        index = ParseFunction(index, stage);
                    }
                }

                if (braceDepth != 0)
                {
                    throw Failure("MSL has an unterminated brace-delimited declaration.");
                }
            }

            private int ParseStruct(int structIndex)
            {
                int nameIndex = structIndex + 1;
                if (nameIndex >= m_Tokens.Count
                    || m_Tokens[nameIndex].Kind != TokenKind.Identifier)
                {
                    throw Failure(
                        $"MSL struct at {m_Tokens[structIndex].Position} has no name.");
                }

                int openBrace = nameIndex + 1;
                while (openBrace < m_Tokens.Count
                       && m_Tokens[openBrace].Text != "{"
                       && m_Tokens[openBrace].Text != ";")
                {
                    ++openBrace;
                }

                if (openBrace >= m_Tokens.Count
                    || m_Tokens[openBrace].Text != "{")
                {
                    throw Failure(
                        $"MSL struct {m_Tokens[nameIndex].Text} has no body.");
                }

                int closeBrace = FindMatching(openBrace, "{", "}");
                List<ParsedDeclaration> members = ParseDeclarations(
                    openBrace + 1,
                    closeBrace,
                    ";");
                ParsedStruct parsed = new(
                    m_Tokens[nameIndex].Text,
                    members);
                if (!m_Structs.TryAdd(parsed.Name, parsed))
                {
                    throw Failure(
                        $"MSL contains duplicate struct declaration {parsed.Name}.");
                }

                return closeBrace;
            }

            private int ParseFunction(
                int stageIndex,
                ShaderExecutionStage stage)
            {
                int openParenthesis = stageIndex + 1;
                while (openParenthesis < m_Tokens.Count
                       && m_Tokens[openParenthesis].Text != "("
                       && m_Tokens[openParenthesis].Text != "{"
                       && m_Tokens[openParenthesis].Text != ";")
                {
                    ++openParenthesis;
                }

                if (openParenthesis >= m_Tokens.Count
                    || m_Tokens[openParenthesis].Text != "(")
                {
                    return stageIndex;
                }

                int nameIndex = openParenthesis - 1;
                if (nameIndex <= stageIndex
                    || m_Tokens[nameIndex].Kind != TokenKind.Identifier)
                {
                    throw Failure(
                        $"MSL stage function at {m_Tokens[stageIndex].Position} "
                        + "has no parseable name.");
                }

                int closeParenthesis = FindMatching(
                    openParenthesis,
                    "(",
                    ")");
                int bodyStart = closeParenthesis + 1;
                while (bodyStart < m_Tokens.Count
                       && m_Tokens[bodyStart].Text != "{"
                       && m_Tokens[bodyStart].Text != ";")
                {
                    ++bodyStart;
                }

                if (bodyStart >= m_Tokens.Count)
                {
                    throw Failure(
                        $"MSL stage function {m_Tokens[nameIndex].Text} has no body.");
                }

                if (m_Tokens[bodyStart].Text == ";")
                {
                    return bodyStart;
                }

                IReadOnlyList<Token> returnType = Slice(
                    stageIndex + 1,
                    nameIndex);
                IReadOnlyList<ParsedAttribute> returnAttributes =
                    ParseAttributes(closeParenthesis + 1, bodyStart);
                List<ParsedDeclaration> parameters = ParseDeclarations(
                    openParenthesis + 1,
                    closeParenthesis,
                    ",");
                m_Functions.Add(new ParsedFunction(
                    m_Tokens[nameIndex].Text,
                    stage,
                    returnType,
                    returnAttributes,
                    parameters));
                return FindMatching(bodyStart, "{", "}");
            }

            private List<ParsedDeclaration> ParseDeclarations(
                int start,
                int end,
                string separator)
            {
                List<ParsedDeclaration> declarations = new();
                int segmentStart = start;
                int parenthesisDepth = 0;
                int bracketDepth = 0;
                int attributeDepth = 0;
                int angleDepth = 0;
                for (int index = start; index < end; ++index)
                {
                    Token token = m_Tokens[index];
                    switch (token.Text)
                    {
                        case "(":
                            ++parenthesisDepth;
                            break;
                        case ")":
                            if (parenthesisDepth == 0)
                            {
                                throw Failure(
                                    $"MSL declaration has unmatched ) at "
                                    + $"{token.Position}.");
                            }

                            --parenthesisDepth;
                            break;
                        case "[":
                            ++bracketDepth;
                            break;
                        case "]":
                            if (bracketDepth == 0)
                            {
                                throw Failure(
                                    $"MSL declaration has unmatched ] at "
                                    + $"{token.Position}.");
                            }

                            --bracketDepth;
                            break;
                        case "<":
                            ++angleDepth;
                            break;
                        case ">":
                            if (angleDepth == 0)
                            {
                                throw Failure(
                                    $"MSL declaration has unmatched > at "
                                    + $"{token.Position}.");
                            }

                            --angleDepth;
                            break;
                    }

                    if (token.Kind == TokenKind.AttributeOpen)
                    {
                        ++attributeDepth;
                    }
                    else if (token.Kind == TokenKind.AttributeClose)
                    {
                        if (attributeDepth == 0)
                        {
                            throw Failure(
                                $"MSL declaration has unmatched ]] at "
                                + $"{token.Position}.");
                        }

                        --attributeDepth;
                    }

                    if (token.Text == separator
                        && parenthesisDepth == 0
                        && bracketDepth == 0
                        && attributeDepth == 0
                        && angleDepth == 0)
                    {
                        ParsedDeclaration? declaration =
                            ParseDeclaration(segmentStart, index);
                        if (declaration is not null)
                        {
                            declarations.Add(declaration);
                        }

                        segmentStart = index + 1;
                    }
                }

                if (parenthesisDepth != 0
                    || bracketDepth != 0
                    || attributeDepth != 0
                    || angleDepth != 0)
                {
                    throw Failure(
                        "MSL declaration has unbalanced delimiters.");
                }

                ParsedDeclaration? finalDeclaration =
                    ParseDeclaration(segmentStart, end);
                if (finalDeclaration is not null)
                {
                    declarations.Add(finalDeclaration);
                }

                return declarations;
            }

            private ParsedDeclaration? ParseDeclaration(int start, int end)
            {
                while (start < end
                       && m_Tokens[start].Text is ";" or ",")
                {
                    ++start;
                }

                if (start >= end)
                {
                    return null;
                }

                List<ParsedAttribute> attributes =
                    ParseAttributes(start, end);
                List<Token> core = new();
                int index = start;
                while (index < end)
                {
                    if (m_Tokens[index].Kind == TokenKind.AttributeOpen)
                    {
                        index = FindMatchingAttribute(index) + 1;
                        continue;
                    }

                    core.Add(m_Tokens[index]);
                    ++index;
                }

                int nameIndex = -1;
                for (int candidate = core.Count - 1; candidate >= 0; --candidate)
                {
                    if (core[candidate].Kind == TokenKind.Identifier)
                    {
                        nameIndex = candidate;
                        break;
                    }
                }

                if (nameIndex <= 0)
                {
                    if (attributes.Count != 0)
                    {
                        throw Failure(
                            $"MSL attributed declaration at "
                            + $"{m_Tokens[start].Position} has no type and name.");
                    }

                    return null;
                }

                return new ParsedDeclaration(
                    core.GetRange(0, nameIndex),
                    attributes);
            }

            private List<ParsedAttribute> ParseAttributes(int start, int end)
            {
                List<ParsedAttribute> attributes = new();
                HashSet<string> names = new(StringComparer.Ordinal);
                for (int index = start; index < end; ++index)
                {
                    if (m_Tokens[index].Kind != TokenKind.AttributeOpen)
                    {
                        continue;
                    }

                    int close = FindMatchingAttribute(index);
                    if (close >= end)
                    {
                        throw Failure(
                            $"MSL attribute at {m_Tokens[index].Position} crosses "
                            + "its declaration boundary.");
                    }

                    int cursor = index + 1;
                    while (cursor < close)
                    {
                        Token name = m_Tokens[cursor];
                        if (name.Kind != TokenKind.Identifier)
                        {
                            throw Failure(
                                $"MSL attribute at {name.Position} has no "
                                + "identifier name.");
                        }

                        ++cursor;
                        List<Token> arguments = new();
                        if (cursor < close && m_Tokens[cursor].Text == "(")
                        {
                            int argumentClose = FindMatching(cursor, "(", ")");
                            if (argumentClose >= close)
                            {
                                throw Failure(
                                    $"MSL attribute {name.Text} at {name.Position} "
                                    + "has malformed arguments.");
                            }

                            for (int argument = cursor + 1;
                                 argument < argumentClose;
                                 ++argument)
                            {
                                arguments.Add(m_Tokens[argument]);
                            }

                            cursor = argumentClose + 1;
                        }

                        if (!names.Add(name.Text))
                        {
                            throw Failure(
                                $"MSL declaration repeats attribute {name.Text} "
                                + $"at {name.Position}.");
                        }

                        attributes.Add(new ParsedAttribute(
                            name.Text,
                            arguments,
                            name));
                        if (cursor == close)
                        {
                            break;
                        }

                        if (m_Tokens[cursor].Text != ",")
                        {
                            throw Failure(
                                $"MSL attribute {name.Text} at {name.Position} "
                                + "is not followed by a comma or attribute close.");
                        }

                        ++cursor;
                    }

                    index = close;
                }

                return attributes;
            }

            private void ReflectOutputDeclaration(
                IReadOnlyList<Token> typeTokens,
                IReadOnlyList<ParsedAttribute> attributes,
                List<MslColorAttachmentIoReflection> colorOutputs,
                ref ShaderDepthExport depthExport,
                ref ShaderStencilExport stencilExport)
            {
                if (!ContainsAttachmentAttribute(attributes))
                {
                    return;
                }

                ValidateAttributeSet(
                    attributes,
                    s_OutputAttachmentAttributes,
                    "fragment output");
                ParsedAttribute? color = FindAttribute(attributes, "color");
                ParsedAttribute? index = FindAttribute(attributes, "index");
                ParsedAttribute? depth = FindAttribute(attributes, "depth");
                ParsedAttribute? stencil = FindAttribute(attributes, "stencil");
                ParsedAttribute? invariant =
                    FindAttribute(attributes, "invariant");
                if (invariant is not null)
                {
                    EnsureNoArguments(invariant);
                }
                ParsedAttribute? texture = FindAttribute(attributes, "texture");
                ParsedAttribute? rasterOrder =
                    FindAttribute(attributes, "raster_order_group");
                if (texture is not null || rasterOrder is not null)
                {
                    throw Failure(
                        "MSL fragment output declaration cannot also be a "
                        + "resource binding.");
                }

                int exportKinds =
                    (color is null ? 0 : 1)
                    + (depth is null ? 0 : 1)
                    + (stencil is null ? 0 : 1);
                if (exportKinds != 1)
                {
                    throw Failure(
                        "MSL fragment output must declare exactly one of color, "
                        + "depth, or stencil.");
                }

                if (color is not null)
                {
                    ValueShape shape = ParseValueShape(typeTokens);
                    colorOutputs.Add(new MslColorAttachmentIoReflection(
                        ShaderStageIoDirection.Output,
                        ReadUInt32(color),
                        index is null ? 0u : ReadUInt32(index),
                        shape.NumericClass,
                        shape.ComponentCount));
                    return;
                }

                if (index is not null)
                {
                    throw Failure(
                        "MSL fragment output index requires a color attribute.");
                }

                ValueShape builtinShape = ParseValueShape(typeTokens);
                if (builtinShape.ComponentCount != 1)
                {
                    throw Failure(
                        "MSL depth/stencil output must be scalar.");
                }

                if (depth is not null)
                {
                    if (builtinShape.NumericClass
                        != ShaderAttachmentNumericClass.FloatingPoint)
                    {
                        throw Failure(
                            "MSL depth output must use a floating-point scalar.");
                    }

                    if (depthExport != ShaderDepthExport.None)
                    {
                        throw Failure("MSL contains more than one depth output.");
                    }

                    depthExport = ReadDepthExport(depth);
                    return;
                }

                if (builtinShape.NumericClass
                    != ShaderAttachmentNumericClass.UnsignedInteger)
                {
                    throw Failure(
                        "MSL stencil reference output must use an unsigned scalar.");
                }

                EnsureNoArguments(stencil!);
                if (stencilExport != ShaderStencilExport.None)
                {
                    throw Failure(
                        "MSL contains more than one stencil reference output.");
                }

                stencilExport = ShaderStencilExport.StencilReference;
            }

            private static void ReflectInputDeclaration(
                IReadOnlyList<Token> typeTokens,
                IReadOnlyList<ParsedAttribute> attributes,
                List<MslColorAttachmentIoReflection> colorInputs)
            {
                ParsedAttribute? color = FindAttribute(attributes, "color");
                if (color is null)
                {
                    return;
                }

                ValidateAttributeSet(
                    attributes,
                    s_InputAttachmentAttributes,
                    "framebuffer-local input");
                foreach (ParsedAttribute attribute in attributes)
                {
                    if (!string.Equals(
                            attribute.Name,
                            "color",
                            StringComparison.Ordinal))
                    {
                        EnsureNoArguments(attribute);
                    }
                }
                if (FindAttribute(attributes, "index") is not null
                    || FindAttribute(attributes, "depth") is not null
                    || FindAttribute(attributes, "stencil") is not null
                    || FindAttribute(attributes, "texture") is not null
                    || FindAttribute(attributes, "raster_order_group") is not null)
                {
                    throw Failure(
                        "MSL framebuffer-local input has conflicting attachment "
                        + "attributes.");
                }

                ValueShape shape = ParseValueShape(typeTokens);
                colorInputs.Add(new MslColorAttachmentIoReflection(
                    ShaderStageIoDirection.Input,
                    ReadUInt32(color),
                    index: 0,
                    shape.NumericClass,
                    shape.ComponentCount));
            }

            private static void ReflectResourceDeclaration(
                IReadOnlyList<Token> typeTokens,
                IReadOnlyList<ParsedAttribute> attributes,
                List<MslTextureBindingReflection> textures,
                List<MslRasterOrderGroupReflection> rasterOrderGroups)
            {
                ParsedAttribute? texture = FindAttribute(attributes, "texture");
                ParsedAttribute? buffer = FindAttribute(attributes, "buffer");
                ParsedAttribute? argumentBufferId = FindAttribute(attributes, "id");
                ParsedAttribute? rasterOrder =
                    FindAttribute(attributes, "raster_order_group");
                if (texture is null
                    && buffer is null
                    && argumentBufferId is null
                    && rasterOrder is null)
                {
                    return;
                }

                if (rasterOrder is not null)
                {
                    HashSet<string> allowedAttributes = texture is not null
                        ? s_RasterOrderTextureAttributes
                        : buffer is not null
                            ? s_RasterOrderBufferAttributes
                            : s_RasterOrderArgumentBufferAttributes;
                    ValidateAttributeSet(
                        attributes,
                        allowedAttributes,
                        "raster-order resource");
                }

                int bindingKinds =
                    (texture is null ? 0 : 1)
                    + (buffer is null ? 0 : 1)
                    + (argumentBufferId is null ? 0 : 1);
                if (bindingKinds != 1)
                {
                    throw Failure(
                        "MSL resource declaration must have exactly one texture, "
                        + "buffer, or id binding attribute.");
                }

                uint? group = rasterOrder is null
                    ? null
                    : ReadUInt32(rasterOrder);
                if (texture is not null)
                {
                    if (!group.HasValue)
                    {
                        return;
                    }

                    uint textureIndex = ReadUInt32(texture);
                    TextureShape shape = ParseTextureShape(typeTokens);
                    if (!shape.IsReadWrite)
                    {
                        throw Failure(
                            $"MSL raster-order texture {textureIndex} must use "
                            + "access::read_write.");
                    }

                    textures.Add(new MslTextureBindingReflection(
                        textureIndex,
                        shape.NumericClass,
                        shape.SampleMode,
                        shape.LayerMode,
                        shape.IsReadWrite,
                        group));
                    if (group.HasValue)
                    {
                        rasterOrderGroups.Add(
                            new MslRasterOrderGroupReflection(
                                MslResourceBindingKind.Texture,
                                textureIndex,
                                group.Value));
                    }

                    return;
                }

                if (!group.HasValue)
                {
                    return;
                }

                ParsedAttribute binding = buffer ?? argumentBufferId!;
                rasterOrderGroups.Add(new MslRasterOrderGroupReflection(
                    buffer is null
                        ? MslResourceBindingKind.ArgumentBufferId
                        : MslResourceBindingKind.Buffer,
                    ReadUInt32(binding),
                    group.Value));
            }

            private string? FindReferencedStruct(
                IReadOnlyList<Token> typeTokens)
            {
                int rootTypeIndex = 0;
                while (rootTypeIndex < typeTokens.Count
                       && typeTokens[rootTypeIndex].Kind == TokenKind.Identifier
                       && s_MslTypeQualifiers.Contains(
                           typeTokens[rootTypeIndex].Text))
                {
                    ++rootTypeIndex;
                }

                if (rootTypeIndex < typeTokens.Count
                    && typeTokens[rootTypeIndex].Kind == TokenKind.Identifier
                    && m_Structs.ContainsKey(typeTokens[rootTypeIndex].Text))
                {
                    if (rootTypeIndex + 1 != typeTokens.Count)
                    {
                        throw Failure(
                            "MSL stage I/O struct type must be an exact root type "
                            + "after known qualifiers.");
                    }

                    return typeTokens[rootTypeIndex].Text;
                }

                for (int index = rootTypeIndex + 1;
                     index < typeTokens.Count;
                     ++index)
                {
                    if (typeTokens[index].Kind == TokenKind.Identifier
                        && m_Structs.ContainsKey(typeTokens[index].Text))
                    {
                        throw Failure(
                            $"MSL stage I/O struct {typeTokens[index].Text} is "
                            + "nested inside a non-struct root type.");
                    }
                }

                return null;
            }

            private int FindMatching(
                int openIndex,
                string open,
                string close)
            {
                int depth = 0;
                for (int index = openIndex; index < m_Tokens.Count; ++index)
                {
                    if (m_Tokens[index].Text == open)
                    {
                        ++depth;
                    }
                    else if (m_Tokens[index].Text == close)
                    {
                        --depth;
                        if (depth == 0)
                        {
                            return index;
                        }
                    }
                }

                throw Failure(
                    $"MSL token {open} at {m_Tokens[openIndex].Position} has "
                    + $"no matching {close}.");
            }

            private int FindMatchingAttribute(int openIndex)
            {
                int depth = 0;
                for (int index = openIndex; index < m_Tokens.Count; ++index)
                {
                    if (m_Tokens[index].Kind == TokenKind.AttributeOpen)
                    {
                        ++depth;
                    }
                    else if (m_Tokens[index].Kind == TokenKind.AttributeClose)
                    {
                        --depth;
                        if (depth == 0)
                        {
                            return index;
                        }
                    }
                }

                throw Failure(
                    $"MSL attribute at {m_Tokens[openIndex].Position} has no "
                    + "closing ]].");
            }

            private IReadOnlyList<Token> Slice(int start, int end)
            {
                List<Token> result = new(end - start);
                for (int index = start; index < end; ++index)
                {
                    result.Add(m_Tokens[index]);
                }

                return result;
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

        private static class Lexer
        {
            public static List<Token> Tokenize(string source)
            {
                List<Token> tokens = new();
                int index = 0;
                int line = 1;
                int column = 1;
                bool lineHasOnlyWhitespace = true;
                while (index < source.Length)
                {
                    char current = source[index];
                    if (char.IsSurrogate(current))
                    {
                        if (!char.IsHighSurrogate(current)
                            || index + 1 >= source.Length
                            || !char.IsLowSurrogate(source[index + 1]))
                        {
                            throw Failure(
                                $"MSL source contains invalid UTF-16 at line "
                                + $"{line}, column {column}.");
                        }

                        throw Failure(
                            $"MSL source contains a non-ASCII code point at line "
                            + $"{line}, column {column}; generated MSL must use "
                            + "portable ASCII identifiers.");
                    }

                    if (current == '\0')
                    {
                        throw Failure(
                            $"MSL source contains NUL at line {line}, column "
                            + $"{column}.");
                    }

                    if (current == '\r' || current == '\n')
                    {
                        ConsumeNewLine(source, ref index, ref line, ref column);
                        lineHasOnlyWhitespace = true;
                        continue;
                    }

                    if (char.IsWhiteSpace(current))
                    {
                        ++index;
                        ++column;
                        continue;
                    }

                    if (current == '#' && lineHasOnlyWhitespace)
                    {
                        SkipPreprocessorDirective(
                            source,
                            ref index,
                            ref line,
                            ref column);
                        lineHasOnlyWhitespace = true;
                        continue;
                    }

                    if (current == '/'
                        && index + 1 < source.Length
                        && source[index + 1] == '/')
                    {
                        SkipLineComment(source, ref index, ref column);
                        continue;
                    }

                    if (current == '/'
                        && index + 1 < source.Length
                        && source[index + 1] == '*')
                    {
                        bool crossedLine = SkipBlockComment(
                            source,
                            ref index,
                            ref line,
                            ref column);
                        if (crossedLine)
                        {
                            lineHasOnlyWhitespace = true;
                        }

                        continue;
                    }

                    lineHasOnlyWhitespace = false;
                    int tokenLine = line;
                    int tokenColumn = column;
                    if (IsIdentifierStart(current))
                    {
                        int start = index++;
                        ++column;
                        while (index < source.Length
                               && IsIdentifierPart(source[index]))
                        {
                            ++index;
                            ++column;
                        }

                        tokens.Add(new Token(
                            TokenKind.Identifier,
                            source[start..index],
                            tokenLine,
                            tokenColumn));
                        continue;
                    }

                    if (char.IsDigit(current))
                    {
                        int start = index++;
                        ++column;
                        while (index < source.Length
                               && (char.IsLetterOrDigit(source[index])
                                   || source[index] == '_'))
                        {
                            ++index;
                            ++column;
                        }

                        tokens.Add(new Token(
                            TokenKind.Number,
                            source[start..index],
                            tokenLine,
                            tokenColumn));
                        continue;
                    }

                    if (current is '"' or '\'')
                    {
                        int start = index;
                        SkipLiteral(
                            source,
                            current,
                            ref index,
                            ref line,
                            ref column);
                        tokens.Add(new Token(
                            TokenKind.Literal,
                            source[start..index],
                            tokenLine,
                            tokenColumn));
                        continue;
                    }

                    if (index + 1 < source.Length)
                    {
                        string pair = source.Substring(index, 2);
                        TokenKind? pairKind = pair switch
                        {
                            "[[" => TokenKind.AttributeOpen,
                            "]]" => TokenKind.AttributeClose,
                            "::" => TokenKind.Scope,
                            _ => null,
                        };
                        if (pairKind.HasValue)
                        {
                            tokens.Add(new Token(
                                pairKind.Value,
                                pair,
                                tokenLine,
                                tokenColumn));
                            index += 2;
                            column += 2;
                            continue;
                        }
                    }

                    tokens.Add(new Token(
                        TokenKind.Symbol,
                        current.ToString(),
                        tokenLine,
                        tokenColumn));
                    ++index;
                    ++column;
                }

                return tokens;
            }

            private static bool IsIdentifierStart(char value)
            {
                return value == '_'
                    || value is >= 'A' and <= 'Z'
                    || value is >= 'a' and <= 'z';
            }

            private static bool IsIdentifierPart(char value)
            {
                return IsIdentifierStart(value)
                    || value is >= '0' and <= '9';
            }

            private static void SkipLineComment(
                string source,
                ref int index,
                ref int column)
            {
                index += 2;
                column += 2;
                while (index < source.Length
                       && source[index] is not '\r' and not '\n')
                {
                    ++index;
                    ++column;
                }
            }

            private static bool SkipBlockComment(
                string source,
                ref int index,
                ref int line,
                ref int column)
            {
                int startLine = line;
                int startColumn = column;
                bool crossedLine = false;
                index += 2;
                column += 2;
                while (index < source.Length)
                {
                    if (source[index] == '*'
                        && index + 1 < source.Length
                        && source[index + 1] == '/')
                    {
                        index += 2;
                        column += 2;
                        return crossedLine;
                    }

                    if (source[index] is '\r' or '\n')
                    {
                        ConsumeNewLine(
                            source,
                            ref index,
                            ref line,
                            ref column);
                        crossedLine = true;
                    }
                    else
                    {
                        ++index;
                        ++column;
                    }
                }

                throw Failure(
                    $"MSL block comment at line {startLine}, column "
                    + $"{startColumn} is unterminated.");
            }

            private static void SkipLiteral(
                string source,
                char delimiter,
                ref int index,
                ref int line,
                ref int column)
            {
                int startLine = line;
                int startColumn = column;
                ++index;
                ++column;
                bool escaped = false;
                while (index < source.Length)
                {
                    char current = source[index];
                    if (current is '\r' or '\n')
                    {
                        throw Failure(
                            $"MSL literal at line {startLine}, column "
                            + $"{startColumn} crosses a line boundary.");
                    }

                    ++index;
                    ++column;
                    if (escaped)
                    {
                        escaped = false;
                        continue;
                    }

                    if (current == '\\')
                    {
                        escaped = true;
                        continue;
                    }

                    if (current == delimiter)
                    {
                        return;
                    }
                }

                throw Failure(
                    $"MSL literal at line {startLine}, column {startColumn} "
                    + "is unterminated.");
            }

            private static void SkipPreprocessorDirective(
                string source,
                ref int index,
                ref int line,
                ref int column)
            {
                int directiveIndex = index + 1;
                while (directiveIndex < source.Length
                       && source[directiveIndex] is ' ' or '\t')
                {
                    ++directiveIndex;
                }

                int directiveStart = directiveIndex;
                while (directiveIndex < source.Length
                       && (char.IsLetter(source[directiveIndex])
                           || source[directiveIndex] == '_'))
                {
                    ++directiveIndex;
                }

                string directive = source[directiveStart..directiveIndex];
                if (directive is "if"
                    or "ifdef"
                    or "ifndef"
                    or "elif"
                    or "else"
                    or "endif")
                {
                    throw Failure(
                        $"MSL conditional preprocessor directive #{directive} "
                        + $"at line {line}, column {column} cannot be "
                        + "reflected safely.");
                }

                bool continued;
                do
                {
                    continued = false;
                    while (index < source.Length
                           && source[index] is not '\r' and not '\n')
                    {
                        continued = source[index] == '\\';
                        ++index;
                        ++column;
                    }

                    if (index < source.Length)
                    {
                        ConsumeNewLine(
                            source,
                            ref index,
                            ref line,
                            ref column);
                    }
                }
                while (continued && index < source.Length);
            }

            private static void ConsumeNewLine(
                string source,
                ref int index,
                ref int line,
                ref int column)
            {
                if (source[index] == '\r'
                    && index + 1 < source.Length
                    && source[index + 1] == '\n')
                {
                    index += 2;
                }
                else
                {
                    ++index;
                }

                ++line;
                column = 1;
            }
        }
    }
}
