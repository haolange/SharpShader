using System;
using System.Collections.Generic;
using System.Globalization;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab.Frontend
{
    internal static class ShaderLabAttachmentInterfaceParser
    {
        public static ShaderAttachmentPhase Parse(
            ShaderLabExtractedAttachmentBlock block)
        {
            ArgumentNullException.ThrowIfNull(block);
            Parser parser = new Parser(block.Content, block.Line, block.Column);
            try
            {
                return parser.ParsePhase();
            }
            catch (ShaderLabParseException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or OverflowException)
            {
                throw new ShaderLabParseException(
                    block.Line,
                    block.Column,
                    $"Invalid AttachmentInterface contract: {exception.Message}");
            }
        }

        private sealed class Parser
        {
            private readonly Tokenizer m_Tokenizer;
            private Token m_Current;

            public Parser(string source, int line, int column)
            {
                m_Tokenizer = new Tokenizer(source, line, column);
                m_Current = m_Tokenizer.Read();
            }

            public ShaderAttachmentPhase ParsePhase()
            {
                bool hasAbiRevision = false;
                bool hasPhase = false;
                bool hasDepthStencilAccess = false;
                bool hasDepthExport = false;
                bool hasStencilExport = false;
                uint abiRevision = 0;
                uint phase = 0;
                ShaderDepthStencilAccess depthStencilAccess = default;
                ShaderDepthExport depthExport = default;
                ShaderStencilExport stencilExport = default;
                List<ShaderAttachmentDeclaration> attachments = new();

                while (m_Current.Kind != TokenKind.End)
                {
                    Token field = Expect(TokenKind.Identifier);
                    switch (field.Text)
                    {
                        case "AbiRevision":
                            RequireFirst(ref hasAbiRevision, field);
                            abiRevision = ReadUInt32("AbiRevision");
                            break;
                        case "Phase":
                            RequireFirst(ref hasPhase, field);
                            phase = ReadUInt32("Phase");
                            break;
                        case "DepthStencilAccess":
                            RequireFirst(ref hasDepthStencilAccess, field);
                            depthStencilAccess = ReadEnum<ShaderDepthStencilAccess>(
                                "DepthStencilAccess");
                            break;
                        case "DepthExport":
                            RequireFirst(ref hasDepthExport, field);
                            depthExport = ReadEnum<ShaderDepthExport>("DepthExport");
                            break;
                        case "StencilExport":
                            RequireFirst(ref hasStencilExport, field);
                            stencilExport = ReadEnum<ShaderStencilExport>("StencilExport");
                            break;
                        case "Attachment":
                            attachments.Add(ParseAttachment());
                            break;
                        default:
                            throw Error(
                                field,
                                $"Unknown AttachmentInterface field '{field.Text}'.");
                    }
                }

                RequirePresent(hasAbiRevision, "AbiRevision");
                RequirePresent(hasPhase, "Phase");
                RequirePresent(hasDepthStencilAccess, "DepthStencilAccess");
                RequirePresent(hasDepthExport, "DepthExport");
                RequirePresent(hasStencilExport, "StencilExport");
                if (abiRevision != ShaderAttachmentInterface.CurrentAbiRevision)
                {
                    throw Error(
                        m_Current,
                        $"Attachment ABI revision must be "
                        + $"{ShaderAttachmentInterface.CurrentAbiRevision}, got "
                        + $"{abiRevision}.");
                }

                return new ShaderAttachmentPhase(
                    phase,
                    attachments,
                    depthStencilAccess,
                    depthExport,
                    stencilExport);
            }

            private ShaderAttachmentDeclaration ParseAttachment()
            {
                Expect(TokenKind.LeftBrace);
                bool hasLogicalAttachmentId = false;
                bool hasInputIndex = false;
                bool hasOutputLocation = false;
                bool hasOutputIndex = false;
                bool hasOutputComponent = false;
                bool hasAspect = false;
                bool hasNumericClass = false;
                bool hasSampleMode = false;
                bool hasLayerMode = false;
                bool hasOrdering = false;
                bool hasFeedback = false;
                bool hasSampledFeedbackBinding = false;
                uint logicalAttachmentId = 0;
                uint? inputIndex = null;
                uint? outputLocation = null;
                uint outputIndex = 0;
                uint outputComponent = 0;
                ShaderAttachmentAspect aspect = default;
                ShaderAttachmentNumericClass numericClass = default;
                ShaderAttachmentSampleMode sampleMode = default;
                ShaderAttachmentLayerMode layerMode = default;
                ShaderAttachmentOrdering ordering = default;
                ShaderAttachmentFeedback feedback = default;
                ShaderBindingKey? sampledFeedbackBinding = null;

                while (m_Current.Kind != TokenKind.RightBrace)
                {
                    if (m_Current.Kind == TokenKind.End)
                    {
                        throw Error(
                            m_Current,
                            "Attachment block is missing its closing brace.");
                    }

                    Token field = Expect(TokenKind.Identifier);
                    switch (field.Text)
                    {
                        case "LogicalAttachmentId":
                            RequireFirst(ref hasLogicalAttachmentId, field);
                            logicalAttachmentId = ReadUInt32("LogicalAttachmentId");
                            break;
                        case "InputIndex":
                            RequireFirst(ref hasInputIndex, field);
                            inputIndex = ReadOptionalUInt32("InputIndex");
                            break;
                        case "OutputLocation":
                            RequireFirst(ref hasOutputLocation, field);
                            outputLocation = ReadOptionalUInt32("OutputLocation");
                            break;
                        case "OutputIndex":
                            RequireFirst(ref hasOutputIndex, field);
                            outputIndex = ReadUInt32("OutputIndex");
                            break;
                        case "OutputComponent":
                            RequireFirst(ref hasOutputComponent, field);
                            outputComponent = ReadUInt32("OutputComponent");
                            break;
                        case "Aspect":
                            RequireFirst(ref hasAspect, field);
                            aspect = ReadAspect();
                            break;
                        case "NumericClass":
                            RequireFirst(ref hasNumericClass, field);
                            numericClass = ReadEnum<ShaderAttachmentNumericClass>(
                                "NumericClass");
                            break;
                        case "SampleMode":
                            RequireFirst(ref hasSampleMode, field);
                            sampleMode = ReadEnum<ShaderAttachmentSampleMode>(
                                "SampleMode");
                            break;
                        case "LayerMode":
                            RequireFirst(ref hasLayerMode, field);
                            layerMode = ReadEnum<ShaderAttachmentLayerMode>(
                                "LayerMode");
                            break;
                        case "Ordering":
                            RequireFirst(ref hasOrdering, field);
                            ordering = ReadEnum<ShaderAttachmentOrdering>("Ordering");
                            break;
                        case "Feedback":
                            RequireFirst(ref hasFeedback, field);
                            feedback = ReadEnum<ShaderAttachmentFeedback>("Feedback");
                            break;
                        case "SampledFeedbackBinding":
                            RequireFirst(ref hasSampledFeedbackBinding, field);
                            sampledFeedbackBinding = ParseSampledFeedbackBinding();
                            break;
                        default:
                            throw Error(
                                field,
                                $"Unknown Attachment field '{field.Text}'.");
                    }
                }

                Expect(TokenKind.RightBrace);
                RequirePresent(hasLogicalAttachmentId, "LogicalAttachmentId");
                RequirePresent(hasInputIndex, "InputIndex");
                RequirePresent(hasOutputLocation, "OutputLocation");
                RequirePresent(hasOutputIndex, "OutputIndex");
                RequirePresent(hasOutputComponent, "OutputComponent");
                RequirePresent(hasAspect, "Aspect");
                RequirePresent(hasNumericClass, "NumericClass");
                RequirePresent(hasSampleMode, "SampleMode");
                RequirePresent(hasLayerMode, "LayerMode");
                RequirePresent(hasOrdering, "Ordering");
                RequirePresent(hasFeedback, "Feedback");
                RequirePresent(
                    hasSampledFeedbackBinding,
                    "SampledFeedbackBinding");
                return new ShaderAttachmentDeclaration(
                    logicalAttachmentId,
                    inputIndex,
                    outputLocation,
                    aspect,
                    numericClass,
                    sampleMode,
                    layerMode,
                    outputIndex,
                    outputComponent,
                    ordering,
                    feedback,
                    sampledFeedbackBinding);
            }

            private ShaderBindingKey? ParseSampledFeedbackBinding()
            {
                if (m_Current.Kind == TokenKind.Identifier
                    && string.Equals(m_Current.Text, "None", StringComparison.Ordinal))
                {
                    Advance();
                    return null;
                }

                Expect(TokenKind.LeftBrace);
                bool hasTable = false;
                bool hasSlot = false;
                bool hasType = false;
                uint table = 0;
                uint slot = 0;
                ShaderBindingClass type = default;
                while (m_Current.Kind != TokenKind.RightBrace)
                {
                    if (m_Current.Kind == TokenKind.End)
                    {
                        throw Error(
                            m_Current,
                            "SampledFeedbackBinding is missing its closing brace.");
                    }

                    Token field = Expect(TokenKind.Identifier);
                    switch (field.Text)
                    {
                        case "Table":
                            RequireFirst(ref hasTable, field);
                            table = ReadUInt32("Table");
                            break;
                        case "Slot":
                            RequireFirst(ref hasSlot, field);
                            slot = ReadUInt32("Slot");
                            break;
                        case "Type":
                            RequireFirst(ref hasType, field);
                            type = ReadEnum<ShaderBindingClass>("Type");
                            break;
                        default:
                            throw Error(
                                field,
                                $"Unknown SampledFeedbackBinding field "
                                + $"'{field.Text}'.");
                    }
                }

                Expect(TokenKind.RightBrace);
                RequirePresent(hasTable, "SampledFeedbackBinding.Table");
                RequirePresent(hasSlot, "SampledFeedbackBinding.Slot");
                RequirePresent(hasType, "SampledFeedbackBinding.Type");
                return new ShaderBindingKey(table, slot, type);
            }

            private ShaderAttachmentAspect ReadAspect()
            {
                Token token = Expect(TokenKind.Identifier);
                return token.Text switch
                {
                    "Color" => ShaderAttachmentAspect.Color,
                    "Depth" => ShaderAttachmentAspect.Depth,
                    "Stencil" => ShaderAttachmentAspect.Stencil,
                    "DepthStencil" => ShaderAttachmentAspect.Depth
                        | ShaderAttachmentAspect.Stencil,
                    _ => throw Error(
                        token,
                        $"Aspect value '{token.Text}' is not defined."),
                };
            }

            private T ReadEnum<T>(string fieldName)
                where T : struct, Enum
            {
                Token token = Expect(TokenKind.Identifier);
                if (!Enum.TryParse(token.Text, ignoreCase: false, out T value)
                    || !Enum.IsDefined(value))
                {
                    throw Error(
                        token,
                        $"{fieldName} value '{token.Text}' is not defined.");
                }

                return value;
            }

            private uint? ReadOptionalUInt32(string fieldName)
            {
                if (m_Current.Kind == TokenKind.Identifier
                    && string.Equals(m_Current.Text, "None", StringComparison.Ordinal))
                {
                    Advance();
                    return null;
                }

                return ReadUInt32(fieldName);
            }

            private uint ReadUInt32(string fieldName)
            {
                Token token = Expect(TokenKind.Number);
                if (!uint.TryParse(
                        token.Text,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out uint value))
                {
                    throw Error(
                        token,
                        $"{fieldName} value '{token.Text}' is not a UInt32.");
                }

                return value;
            }

            private Token Expect(TokenKind kind)
            {
                Token token = m_Current;
                if (token.Kind != kind)
                {
                    throw Error(
                        token,
                        $"Expected {kind}, got {token.Kind} '{token.Text}'.");
                }

                Advance();
                return token;
            }

            private void Advance()
            {
                m_Current = m_Tokenizer.Read();
            }

            private static void RequireFirst(ref bool seen, Token field)
            {
                if (seen)
                {
                    throw Error(
                        field,
                        $"AttachmentInterface field '{field.Text}' is duplicated.");
                }

                seen = true;
            }

            private void RequirePresent(bool present, string fieldName)
            {
                if (!present)
                {
                    throw Error(
                        m_Current,
                        $"AttachmentInterface field '{fieldName}' is required.");
                }
            }

            private static ShaderLabParseException Error(
                Token token,
                string message)
            {
                return new ShaderLabParseException(
                    token.Line,
                    token.Column,
                    message);
            }
        }

        private enum TokenKind : byte
        {
            Identifier,
            Number,
            LeftBrace,
            RightBrace,
            End,
        }

        private readonly struct Token
        {
            public TokenKind Kind { get; }
            public string Text { get; }
            public int Line { get; }
            public int Column { get; }

            public Token(TokenKind kind, string text, int line, int column)
            {
                Kind = kind;
                Text = text;
                Line = line;
                Column = column;
            }
        }

        private sealed class Tokenizer
        {
            private readonly string m_Source;
            private int m_Index;
            private int m_Line;
            private int m_Column;

            public Tokenizer(string source, int line, int column)
            {
                m_Source = source;
                m_Line = line;
                m_Column = column;
            }

            public Token Read()
            {
                SkipTrivia();
                if (m_Index >= m_Source.Length)
                {
                    return new Token(TokenKind.End, string.Empty, m_Line, m_Column);
                }

                int line = m_Line;
                int column = m_Column;
                char current = m_Source[m_Index];
                if (current == '{')
                {
                    Advance();
                    return new Token(TokenKind.LeftBrace, "{", line, column);
                }

                if (current == '}')
                {
                    Advance();
                    return new Token(TokenKind.RightBrace, "}", line, column);
                }

                if (char.IsLetter(current) || current == '_')
                {
                    int start = m_Index;
                    do
                    {
                        Advance();
                    }
                    while (m_Index < m_Source.Length
                        && (char.IsLetterOrDigit(m_Source[m_Index])
                            || m_Source[m_Index] == '_'));
                    return new Token(
                        TokenKind.Identifier,
                        m_Source.Substring(start, m_Index - start),
                        line,
                        column);
                }

                if (char.IsDigit(current))
                {
                    int start = m_Index;
                    do
                    {
                        Advance();
                    }
                    while (m_Index < m_Source.Length
                        && char.IsDigit(m_Source[m_Index]));
                    return new Token(
                        TokenKind.Number,
                        m_Source.Substring(start, m_Index - start),
                        line,
                        column);
                }

                throw new ShaderLabParseException(
                    line,
                    column,
                    $"Unexpected AttachmentInterface character '{current}'.");
            }

            private void SkipTrivia()
            {
                while (m_Index < m_Source.Length)
                {
                    char current = m_Source[m_Index];
                    char next = m_Index + 1 < m_Source.Length
                        ? m_Source[m_Index + 1]
                        : '\0';
                    if (char.IsWhiteSpace(current))
                    {
                        Advance();
                        continue;
                    }

                    if (current == '/' && next == '/')
                    {
                        Advance();
                        Advance();
                        while (m_Index < m_Source.Length
                            && m_Source[m_Index] != '\n')
                        {
                            Advance();
                        }
                        continue;
                    }

                    if (current == '/' && next == '*')
                    {
                        int line = m_Line;
                        int column = m_Column;
                        Advance();
                        Advance();
                        bool closed = false;
                        while (m_Index < m_Source.Length)
                        {
                            if (m_Source[m_Index] == '*'
                                && m_Index + 1 < m_Source.Length
                                && m_Source[m_Index + 1] == '/')
                            {
                                Advance();
                                Advance();
                                closed = true;
                                break;
                            }
                            Advance();
                        }

                        if (!closed)
                        {
                            throw new ShaderLabParseException(
                                line,
                                column,
                                "AttachmentInterface block comment is not closed.");
                        }
                        continue;
                    }

                    break;
                }
            }

            private void Advance()
            {
                if (m_Source[m_Index++] == '\n')
                {
                    ++m_Line;
                    m_Column = 1;
                }
                else
                {
                    ++m_Column;
                }
            }
        }
    }
}