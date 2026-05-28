using System;
using System.Text;
using System.Collections.Generic;

namespace SharpShader.ShaderLab.Frontend
{
    internal sealed class ShaderLabPreprocessedSource
    {
        private readonly Dictionary<string, string> m_HlslBlocks;

        public string SanitizedSource { get; }

        public ShaderLabPreprocessedSource(string sanitizedSource, Dictionary<string, string> hlslBlocks)
        {
            SanitizedSource = sanitizedSource;
            m_HlslBlocks = hlslBlocks;
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

        public static ShaderLabPreprocessedSource Extract(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return new ShaderLabPreprocessedSource(string.Empty, new Dictionary<string, string>(StringComparer.Ordinal));
            }

            Dictionary<string, string> hlslBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
            StringBuilder sanitized = new StringBuilder(source.Length + 64);

            int cursor = 0;
            while (cursor < source.Length)
            {
                int keywordIndex = FindKeywordOutsideCommentsAndStrings(source, HlslProgramKeyword, cursor);
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
                    throw new ShaderLabParseException(line, column, "HLSLPROGRAM 缺少匹配的 ENDHLSL。");
                }

                string programBody = source.Substring(programBodyStart, endHlslIndex - programBodyStart);
                string placeholder = $"__HLSL_BLOCK_{hlslBlocks.Count}__";
                hlslBlocks.Add(placeholder, programBody);

                sanitized.Append(' ');
                sanitized.Append(placeholder);
                sanitized.Append(' ');

                for (int i = 0; i < programBody.Length; i++)
                {
                    if (programBody[i] == '\n')
                    {
                        sanitized.Append('\n');
                    }
                }

                cursor = endHlslIndex;
            }

            return new ShaderLabPreprocessedSource(sanitized.ToString(), hlslBlocks);
        }

        private static bool TryFindEndHlsl(string source, int startIndex, out int endHlslIndex)
        {
            endHlslIndex = -1;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;

            for (int i = startIndex; i < source.Length; i++)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

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
                        i++;
                    }

                    continue;
                }

                if (!inString && current == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (current == '"' && (i == 0 || source[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (IsKeywordAt(source, EndHlslKeyword, i))
                {
                    endHlslIndex = i;
                    return true;
                }
            }

            return false;
        }

        private static int FindKeywordOutsideCommentsAndStrings(string source, string keyword, int startIndex)
        {
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;

            for (int i = Math.Max(startIndex, 0); i < source.Length; i++)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

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
                        i++;
                    }

                    continue;
                }

                if (!inString && current == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (current == '"' && (i == 0 || source[i - 1] != '\\'))
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                {
                    continue;
                }

                if (IsKeywordAt(source, keyword, i))
                {
                    return i;
                }
            }

            return -1;
        }

        private static bool IsKeywordAt(string source, string keyword, int index)
        {
            if (index < 0 || index + keyword.Length > source.Length)
            {
                return false;
            }

            for (int i = 0; i < keyword.Length; i++)
            {
                if (char.ToUpperInvariant(source[index + i]) != keyword[i])
                {
                    return false;
                }
            }

            bool leftBoundary = index == 0 || !IsIdentifierChar(source[index - 1]);
            int after = index + keyword.Length;
            bool rightBoundary = after >= source.Length || !IsIdentifierChar(source[after]);
            return leftBoundary && rightBoundary;
        }

        private static bool IsIdentifierChar(char c)
        {
            return char.IsLetterOrDigit(c) || c == '_';
        }

        private static (int Line, int Column) GetLineColumn(string source, int index)
        {
            int line = 1;
            int column = 1;
            int end = Math.Min(index, source.Length);
            for (int i = 0; i < end; i++)
            {
                if (source[i] == '\n')
                {
                    line++;
                    column = 1;
                }
                else
                {
                    column++;
                }
            }

            return (line, column);
        }
    }
}
