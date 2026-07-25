using System;
using System.Text;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace SharpShader.ShaderLab.Frontend
{
    internal sealed class ShaderLabExtractedAttachmentBlock
    {
        public int StartIndex { get; }
        public int EndIndex { get; }
        public int Line { get; }
        public int Column { get; }
        public string Content { get; }

        public ShaderLabExtractedAttachmentBlock(
            int startIndex,
            int endIndex,
            int line,
            int column,
            string content)
        {
            StartIndex = startIndex;
            EndIndex = endIndex;
            Line = line;
            Column = column;
            Content = content;
        }
    }

    internal sealed class ShaderLabPreprocessedSource
    {
        private readonly Dictionary<string, string> m_HlslBlocks;
        private readonly ReadOnlyCollection<ShaderLabExtractedAttachmentBlock>
            m_AttachmentBlocks;

        public string SanitizedSource { get; }
        public IReadOnlyList<ShaderLabExtractedAttachmentBlock> AttachmentBlocks =>
            m_AttachmentBlocks;

        public ShaderLabPreprocessedSource(
            string sanitizedSource,
            Dictionary<string, string> hlslBlocks,
            List<ShaderLabExtractedAttachmentBlock> attachmentBlocks)
        {
            SanitizedSource = sanitizedSource;
            m_HlslBlocks = hlslBlocks;
            m_AttachmentBlocks = attachmentBlocks.AsReadOnly();
        }

        public bool TryGetHlslBlock(string placeholder, out string? source)
        {
            return m_HlslBlocks.TryGetValue(placeholder, out source);
        }
    }

    internal static class ShaderLabProgramBlockExtractor
    {
        private const string HlslProgramKeyword = "HLSLPROGRAM";
        private const string EndHlslKeyword = "ENDHLSL";
        private const string AttachmentInterfaceKeyword = "ATTACHMENTINTERFACE";

        public static ShaderLabPreprocessedSource Extract(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return new ShaderLabPreprocessedSource(
                    string.Empty,
                    new Dictionary<string, string>(StringComparer.Ordinal),
                    new List<ShaderLabExtractedAttachmentBlock>());
            }

            Dictionary<string, string> hlslBlocks =
                new Dictionary<string, string>(StringComparer.Ordinal);
            string hlslSanitized = ExtractHlslBlocks(source, hlslBlocks);
            List<ShaderLabExtractedAttachmentBlock> attachmentBlocks = new();
            string sanitized = ExtractAttachmentBlocks(
                hlslSanitized,
                attachmentBlocks);
            return new ShaderLabPreprocessedSource(
                sanitized,
                hlslBlocks,
                attachmentBlocks);
        }

        private static string ExtractHlslBlocks(
            string source,
            Dictionary<string, string> hlslBlocks)
        {
            StringBuilder sanitized = new StringBuilder(source.Length + 64);
            int cursor = 0;
            while (cursor < source.Length)
            {
                int keywordIndex = FindKeywordOutsideCommentsAndStrings(
                    source,
                    HlslProgramKeyword,
                    cursor);
                if (keywordIndex < 0)
                {
                    sanitized.Append(source, cursor, source.Length - cursor);
                    break;
                }

                sanitized.Append(source, cursor, keywordIndex - cursor);
                sanitized.Append(HlslProgramKeyword);

                int programBodyStart = keywordIndex + HlslProgramKeyword.Length;
                if (!TryFindEndHlsl(source, programBodyStart, out int endHlslIndex))
                {
                    (int line, int column) = GetLineColumn(source, keywordIndex);
                    throw new ShaderLabParseException(
                        line,
                        column,
                        "HLSLPROGRAM is missing its matching ENDHLSL.");
                }

                string programBody = source.Substring(
                    programBodyStart,
                    endHlslIndex - programBodyStart);
                string placeholder = $"__HLSL_BLOCK_{hlslBlocks.Count}__";
                hlslBlocks.Add(placeholder, programBody);

                sanitized.Append(' ');
                sanitized.Append(placeholder);
                sanitized.Append(' ');
                for (int index = 0; index < programBody.Length; ++index)
                {
                    if (programBody[index] == '\n')
                    {
                        sanitized.Append('\n');
                    }
                }

                cursor = endHlslIndex;
            }

            return sanitized.ToString();
        }

        private static string ExtractAttachmentBlocks(
            string source,
            List<ShaderLabExtractedAttachmentBlock> attachmentBlocks)
        {
            StringBuilder sanitized = new StringBuilder(source);
            int cursor = 0;
            while (cursor < source.Length)
            {
                int keywordIndex = FindKeywordOutsideCommentsAndStrings(
                    source,
                    AttachmentInterfaceKeyword,
                    cursor);
                if (keywordIndex < 0)
                {
                    break;
                }

                int openBraceIndex = keywordIndex + AttachmentInterfaceKeyword.Length;
                while (openBraceIndex < source.Length
                    && char.IsWhiteSpace(source[openBraceIndex]))
                {
                    ++openBraceIndex;
                }

                if (openBraceIndex >= source.Length
                    || source[openBraceIndex] != '{')
                {
                    (int line, int column) = GetLineColumn(source, keywordIndex);
                    throw new ShaderLabParseException(
                        line,
                        column,
                        "AttachmentInterface must be followed by a braced contract block.");
                }

                if (!TryFindMatchingBrace(
                        source,
                        openBraceIndex,
                        out int closeBraceIndex))
                {
                    (int line, int column) = GetLineColumn(source, keywordIndex);
                    throw new ShaderLabParseException(
                        line,
                        column,
                        "AttachmentInterface is missing its matching closing brace.");
                }

                (int blockLine, int blockColumn) = GetLineColumn(
                    source,
                    keywordIndex);
                attachmentBlocks.Add(new ShaderLabExtractedAttachmentBlock(
                    keywordIndex,
                    closeBraceIndex,
                    blockLine,
                    blockColumn,
                    source.Substring(
                        openBraceIndex + 1,
                        closeBraceIndex - openBraceIndex - 1)));

                for (int index = keywordIndex; index <= closeBraceIndex; ++index)
                {
                    if (source[index] != '\r' && source[index] != '\n')
                    {
                        sanitized[index] = ' ';
                    }
                }

                cursor = closeBraceIndex + 1;
            }

            return sanitized.ToString();
        }

        private static bool TryFindEndHlsl(
            string source,
            int startIndex,
            out int endHlslIndex)
        {
            endHlslIndex = FindKeywordOutsideCommentsAndStrings(
                source,
                EndHlslKeyword,
                startIndex);
            return endHlslIndex >= 0;
        }

        private static bool TryFindMatchingBrace(
            string source,
            int openBraceIndex,
            out int closeBraceIndex)
        {
            int depth = 0;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool escaped = false;
            for (int index = openBraceIndex; index < source.Length; ++index)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';
                if (inLineComment)
                {
                    if (current == '\n')
                    {
                        inLineComment = false;
                    }
                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        ++index;
                    }
                    continue;
                }

                if (!inString && current == '/' && next == '/')
                {
                    inLineComment = true;
                    ++index;
                    continue;
                }

                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    ++index;
                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (current == '{')
                {
                    ++depth;
                }
                else if (current == '}' && --depth == 0)
                {
                    closeBraceIndex = index;
                    return true;
                }
            }

            closeBraceIndex = -1;
            return false;
        }

        private static int FindKeywordOutsideCommentsAndStrings(
            string source,
            string keyword,
            int startIndex)
        {
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool escaped = false;
            for (int index = Math.Max(startIndex, 0); index < source.Length; ++index)
            {
                char current = source[index];
                char next = index + 1 < source.Length ? source[index + 1] : '\0';
                if (inLineComment)
                {
                    if (current == '\n')
                    {
                        inLineComment = false;
                    }
                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        ++index;
                    }
                    continue;
                }

                if (!inString && current == '/' && next == '/')
                {
                    inLineComment = true;
                    ++index;
                    continue;
                }

                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    ++index;
                    continue;
                }

                if (inString)
                {
                    if (escaped)
                    {
                        escaped = false;
                    }
                    else if (current == '\\')
                    {
                        escaped = true;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (current == '"')
                {
                    inString = true;
                    continue;
                }

                if (IsKeywordAt(source, keyword, index))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool IsKeywordAt(
            string source,
            string keyword,
            int index)
        {
            if (index < 0 || index + keyword.Length > source.Length)
            {
                return false;
            }

            for (int offset = 0; offset < keyword.Length; ++offset)
            {
                if (char.ToUpperInvariant(source[index + offset]) != keyword[offset])
                {
                    return false;
                }
            }

            bool leftBoundary = index == 0
                || !IsIdentifierChar(source[index - 1]);
            int after = index + keyword.Length;
            bool rightBoundary = after >= source.Length
                || !IsIdentifierChar(source[after]);
            return leftBoundary && rightBoundary;
        }

        private static bool IsIdentifierChar(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static (int Line, int Column) GetLineColumn(
            string source,
            int index)
        {
            int line = 1;
            int column = 1;
            int end = Math.Min(index, source.Length);
            for (int offset = 0; offset < end; ++offset)
            {
                if (source[offset] == '\n')
                {
                    ++line;
                    column = 1;
                }
                else
                {
                    ++column;
                }
            }

            return (line, column);
        }
    }
}