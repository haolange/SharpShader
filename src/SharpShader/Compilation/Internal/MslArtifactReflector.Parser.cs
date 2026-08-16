using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal static partial class MslArtifactReflector
    {

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

            private static void ReflectOutputDeclaration(
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
}
}
