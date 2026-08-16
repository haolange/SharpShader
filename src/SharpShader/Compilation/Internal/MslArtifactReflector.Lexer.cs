using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using SharpShader.HLSLCrossCompiler;

namespace SharpShader.Compilation.Internal
{

    internal static partial class MslArtifactReflector
    {

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
