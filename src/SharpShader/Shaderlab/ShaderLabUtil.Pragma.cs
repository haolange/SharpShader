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
        private static ShaderLabProgram ParseProgram(string programSource)
        {
            return new ShaderLabProgram(
                programSource.Trim(),
                ParseProgramEntries(programSource),
                ParseKeywordGroups(programSource));
        }

        private static List<ShaderLabProgramEntry> ParseProgramEntries(string programSource)
        {
            List<ShaderLabProgramEntry> entries = new List<ShaderLabProgramEntry>();
            string pragmaSource = StripCommentsPreserveNewlines(programSource);
            foreach (Match match in s_PragmaRegex.Matches(pragmaSource))
            {
                string stageKeyword = match.Groups["stage"].Value;
                string entryName = match.Groups["entry"].Value;
                string directive = stageKeyword.Trim().ToLowerInvariant();
                EShaderLabShaderStage stage = ParseStage(stageKeyword);
                if (stage != EShaderLabShaderStage.Undefined)
                {
                    entries.Add(new ShaderLabProgramEntry(stage, entryName));
                    continue;
                }

                if (directive.Equals("multi_compile", StringComparison.Ordinal)
                    || IsPassthroughHlslPragma(directive))
                {
                    continue;
                }

                // Unknown HLSL pragmas that happen to match "word identifier"
                // stay passthrough. Owned stage names never reach here.
            }

            return entries;
        }

        private static StandaloneShaderProgram ParseStandaloneProgram(string source, string sourcePath, StandaloneShaderProgramKind kind)
        {
            string normalizedSource = NormalizeSource(source);
            return new StandaloneShaderProgram(
                kind,
                sourcePath,
                normalizedSource.Trim(),
                ParseStandaloneEntries(normalizedSource, kind),
                ParseKeywordGroups(normalizedSource));
        }

        private static List<StandaloneShaderEntry> ParseStandaloneEntries(string source, StandaloneShaderProgramKind kind)
        {
            List<StandaloneShaderEntry> entries = new List<StandaloneShaderEntry>();
            string pragmaSource = StripCommentsPreserveNewlines(source);
            foreach (Match match in s_StandalonePragmaRegex.Matches(pragmaSource))
            {
                string directive = match.Groups["directive"].Value.Trim().ToLowerInvariant();
                string args = match.Groups["args"].Value.Trim();
                (int line, int column) = GetSourcePosition(pragmaSource, match.Index);

                if (directive.Equals("multi_compile", StringComparison.Ordinal)
                    || IsPassthroughHlslPragma(directive))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(args))
                {
                    if (IsOwnedStandaloneDirective(kind, directive))
                    {
                        throw ParseError(
                            line,
                            column,
                            $"Standalone {kind} #pragma {directive} requires an entry-point name.");
                    }

                    continue;
                }

                string[] tokens = args.Split(s_PragmaArgumentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    throw ParseError(
                        line,
                        column,
                        $"Standalone {kind} #pragma {directive} requires an entry-point name.");
                }

                if (kind == StandaloneShaderProgramKind.Compute)
                {
                    if (directive.Equals("kernel", StringComparison.Ordinal)
                        || directive.Equals("compute", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry(
                            StandaloneShaderStage.Compute,
                            tokens[0]));
                        continue;
                    }

                    throw ParseError(
                        line,
                        column,
                        $"Unrecognized compute #pragma '{directive}'.");
                }

                if (kind == StandaloneShaderProgramKind.RayTrace)
                {
                    if (directive.Equals("raygeneration", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry(
                            StandaloneShaderStage.RayGeneration,
                            tokens[0]));
                        continue;
                    }

                    if (directive.Equals("miss", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry(
                            StandaloneShaderStage.Miss,
                            tokens[0]));
                        continue;
                    }

                    if (directive.Equals("callable", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry(
                            StandaloneShaderStage.Callable,
                            tokens[0]));
                        continue;
                    }

                    throw ParseError(
                        line,
                        column,
                        $"Unrecognized raytrace #pragma '{directive}'.");
                }
            }

            return entries;
        }

        private static bool IsOwnedStandaloneDirective(StandaloneShaderProgramKind kind, string directive)
        {
            return kind == StandaloneShaderProgramKind.Compute
                ? directive is "kernel" or "compute"
                : directive is "raygeneration" or "miss" or "callable";
        }

        private static List<ShaderKeywordGroup> ParseKeywordGroups(string source)
        {
            List<ShaderKeywordGroup> groups = new List<ShaderKeywordGroup>();
            string pragmaSource = StripCommentsPreserveNewlines(source);
            foreach (Match match in s_StandalonePragmaRegex.Matches(pragmaSource))
            {
                string directive = match.Groups["directive"].Value.Trim().ToLowerInvariant();
                if (!directive.Equals("multi_compile", StringComparison.Ordinal))
                {
                    continue;
                }

                string args = match.Groups["args"].Value.Trim();
                if (string.IsNullOrWhiteSpace(args))
                {
                    (int line, int column) = GetSourcePosition(pragmaSource, match.Index);
                    throw ParseError(line, column, "#pragma multi_compile requires at least one keyword.");
                }

                string[] tokens = args.Split(s_PragmaArgumentSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    (int line, int column) = GetSourcePosition(pragmaSource, match.Index);
                    throw ParseError(line, column, "#pragma multi_compile requires at least one keyword.");
                }

                groups.Add(new ShaderKeywordGroup(tokens));
            }

            return groups;
        }
    }
}
