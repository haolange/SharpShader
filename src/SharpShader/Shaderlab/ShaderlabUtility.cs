using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using SharpShader.ShaderLab.Frontend;
using System.Text.RegularExpressions;

namespace SharpShader.ShaderLab
{
    public static partial class ShaderLabUtil
    {
        private static readonly Regex s_PragmaRegex = new Regex(@"^\s*#pragma\s+(?<stage>\w+)\s+(?<entry>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex s_StandalonePragmaRegex = new Regex(@"^\s*#pragma\s+(?<directive>\w+)(?<args>.*)$", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly char[] s_PragmaArgumentSeparators = [' ', '\t'];

        public static ShaderLab ParseShaderLabFromFile(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            string source = File.ReadAllText(fullPath);
            return ParseShaderLabFromSource(source, fullPath);
        }

        public static ShaderLab ParseShaderLabFromSource(string source, string sourcePath = "<memory>")
        {
            string normalizedSource = NormalizeSource(source);
            ShaderLabPreprocessedSource preprocessed = ShaderLabProgramBlockExtractor.Extract(normalizedSource);
            ShaderLabDslParseResult parsed = ShaderLabDslParser.Parse(preprocessed.SanitizedSource);

            Dictionary<string, string> tags = new(StringComparer.Ordinal);
            foreach (ShaderLabParsedTag tag in parsed.ShaderTags)
            {
                tags[tag.Key] = tag.Value;
            }

            List<ShaderLabPass> passes = new();
            HashSet<int> claimedAttachmentBlocks = new();
            foreach (ShaderLabParsedPass parsedPass in parsed.Passes)
            {
                Dictionary<string, string> passTags = new(StringComparer.Ordinal);
                foreach (ShaderLabParsedTag tag in parsedPass.Tags)
                {
                    passTags[tag.Key] = tag.Value;
                }

                ShaderLabExtractedAttachmentBlock? attachmentBlock = null;
                for (int blockIndex = 0;
                     blockIndex < preprocessed.AttachmentBlocks.Count;
                     ++blockIndex)
                {
                    ShaderLabExtractedAttachmentBlock candidate =
                        preprocessed.AttachmentBlocks[blockIndex];
                    if (candidate.StartIndex < parsedPass.SourceStartIndex
                        || candidate.EndIndex > parsedPass.SourceEndIndex)
                    {
                        continue;
                    }

                    if (attachmentBlock is not null)
                    {
                        throw new ShaderLabParseException(
                            candidate.Line,
                            candidate.Column,
                            "A ShaderLab Pass may contain only one "
                            + "AttachmentInterface block.");
                    }

                    attachmentBlock = candidate;
                    claimedAttachmentBlocks.Add(blockIndex);
                }

                ShaderLabProgram program = new ShaderLabProgram(string.Empty, Array.Empty<ShaderLabProgramEntry>());
                if (!string.IsNullOrWhiteSpace(parsedPass.HlslPlaceholder))
                {
                    if (!preprocessed.TryGetHlslBlock(parsedPass.HlslPlaceholder, out string? programSource))
                    {
                        throw new FormatException($"ShaderLab placeholder '{parsedPass.HlslPlaceholder}' has no extracted HLSL block.");
                    }

                    program = ParseProgram(programSource ?? string.Empty);
                }

                if (attachmentBlock is not null)
                {
                    program = new ShaderLabProgram(
                        program.Source,
                        program.Entries,
                        program.KeywordGroups,
                        ShaderLabAttachmentInterfaceParser.Parse(attachmentBlock));
                }

                passes.Add(new ShaderLabPass(
                    program,
                    passTags,
                    ParseRenderState(parsedPass.StateSource, parsedPass.StencilBlockContent)));
            }

            if (claimedAttachmentBlocks.Count
                != preprocessed.AttachmentBlocks.Count)
            {
                for (int blockIndex = 0;
                     blockIndex < preprocessed.AttachmentBlocks.Count;
                     ++blockIndex)
                {
                    if (claimedAttachmentBlocks.Contains(blockIndex))
                    {
                        continue;
                    }

                    ShaderLabExtractedAttachmentBlock unclaimed =
                        preprocessed.AttachmentBlocks[blockIndex];
                    throw new ShaderLabParseException(
                        unclaimed.Line,
                        unclaimed.Column,
                        "AttachmentInterface is valid only inside a ShaderLab Pass.");
                }
            }

            return new ShaderLab(
                parsed.Name,
                sourcePath,
                passes,
                tags,
                ParseShaderLabPropertiesBlock(parsed.PropertiesBlockContent));
        }

        public static StandaloneShaderProgram ParseComputeProgramFromFile(string filePath)
        {
            string source = File.ReadAllText(filePath);
            return ParseComputeProgramFromSource(source, filePath);
        }

        public static StandaloneShaderProgram ParseComputeProgramFromSource(string source, string sourcePath = "<memory>")
        {
            return ParseStandaloneProgram(source, sourcePath, StandaloneShaderProgramKind.Compute);
        }

        public static StandaloneShaderProgram ParseRayTraceProgramFromFile(string filePath)
        {
            string source = File.ReadAllText(filePath);
            return ParseRayTraceProgramFromSource(source, filePath);
        }

        public static StandaloneShaderProgram ParseRayTraceProgramFromSource(string source, string sourcePath = "<memory>")
        {
            return ParseStandaloneProgram(source, sourcePath, StandaloneShaderProgramKind.RayTrace);
        }





        internal static ShaderLabParseException ParseError(int line, int column, string message)
        {
            return new ShaderLabParseException(line, column, message);
        }

        internal static (int Line, int Column) GetSourcePosition(string source, int index)
        {
            int line = 1;
            int column = 1;
            int limit = Math.Clamp(index, 0, source.Length);
            for (int i = 0; i < limit; ++i)
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

        internal static bool IsPassthroughHlslPragma(string directive)
        {
            return directive switch
            {
                "warning" or "once" or "pack_matrix" or "enable" or "disable"
                    or "hlsl" or "dxc" or "target" or "only_renderers"
                    or "exclude_renderers" or "skip_optimizations"
                    or "disable_optimizations" or "hardware" or "instancing"
                    or "exclude" or "include" or "shader_feature"
                    or "editor_sync_compilation" or "skip_variants" => true,
                _ => false,
            };
        }

        private static int FindMatchingBracket(string source, int openIndex, char openChar, char closeChar)
        {
            if (openIndex < 0 || openIndex >= source.Length || source[openIndex] != openChar)
            {
                return -1;
            }

            int depth = 0;
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;

            for (int i = openIndex; i < source.Length; i++)
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
                }

                if (inString)
                {
                    continue;
                }

                if (current == openChar)
                {
                    depth++;
                }
                else if (current == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        private static string RemoveInlineComment(string line)
        {
            bool inString = false;
            for (int i = 0; i < line.Length - 1; i++)
            {
                if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
                {
                    inString = !inString;
                }

                if (!inString && line[i] == '/' && line[i + 1] == '/')
                {
                    return line.Substring(0, i);
                }
            }

            return line;
        }

        private static string StripCommentsPreserveNewlines(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }

            StringBuilder builder = new StringBuilder(source.Length);
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;

            for (int i = 0; i < source.Length; i++)
            {
                char current = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (inLineComment)
                {
                    if (current == '\n')
                    {
                        inLineComment = false;
                        builder.Append('\n');
                    }
                    else
                    {
                        builder.Append(' ');
                    }

                    continue;
                }

                if (inBlockComment)
                {
                    if (current == '*' && next == '/')
                    {
                        inBlockComment = false;
                        builder.Append("  ");
                        i++;
                        continue;
                    }

                    builder.Append(current == '\n' ? '\n' : ' ');
                    continue;
                }

                if (!inString && current == '/' && next == '/')
                {
                    inLineComment = true;
                    builder.Append("  ");
                    i++;
                    continue;
                }

                if (!inString && current == '/' && next == '*')
                {
                    inBlockComment = true;
                    builder.Append("  ");
                    i++;
                    continue;
                }

                if (current == '"' && (i == 0 || source[i - 1] != '\\'))
                {
                    inString = !inString;
                }

                builder.Append(current);
            }

            return builder.ToString();
        }

        private static string NormalizeSource(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }

            string normalized = source.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            if (normalized.Length > 0 && normalized[0] == '\uFEFF')
            {
                normalized = normalized.Substring(1);
            }

            StringBuilder builder = new StringBuilder(normalized.Length);
            foreach (char c in normalized)
            {
                builder.Append(c == '\t' ? ' ' : c);
            }

            return builder.ToString();
        }

    }
}
