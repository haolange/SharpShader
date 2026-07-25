using System;
using System.IO;
using System.Text;
using System.Globalization;
using System.Collections.Generic;
using SharpShader.ShaderLab.Frontend;
using System.Text.RegularExpressions;

namespace SharpShader.ShaderLab
{
    public static class ShaderLabUtil
    {
        private static readonly Regex s_PragmaRegex = new Regex(@"^\s*#pragma\s+(?<stage>\w+)\s+(?<entry>[A-Za-z_]\w*)", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex s_StandalonePragmaRegex = new Regex(@"^\s*#pragma\s+(?<directive>\w+)(?<args>.*)$", RegexOptions.Compiled | RegexOptions.Multiline);

        public static ShaderLab ParseShaderLabFromFile(string filePath)
        {
            string fullPath = Path.GetFullPath(filePath);
            string source = File.ReadAllText(fullPath);
            ShaderLab shaderLab = ParseShaderLabFromSource(source);
            shaderLab.SourcePath = fullPath;
            return shaderLab;
        }

        public static ShaderLab ParseShaderLabFromSource(string source)
        {
            string normalizedSource = NormalizeSource(source);
            ShaderLabPreprocessedSource preprocessed = ShaderLabProgramBlockExtractor.Extract(normalizedSource);
            ShaderLabDslParseResult parsed = ShaderLabDslParser.Parse(preprocessed.SanitizedSource);

            ShaderLab shaderLab = new ShaderLab
            {
                Name = parsed.Name,
                Properties = ParseShaderLabPropertiesBlock(parsed.PropertiesBlockContent),
            };

            foreach (ShaderLabParsedTag tag in parsed.ShaderTags)
            {
                shaderLab.Tags[tag.Key] = tag.Value;
            }

            HashSet<int> claimedAttachmentBlocks = new();
            foreach (ShaderLabParsedPass parsedPass in parsed.Passes)
            {
                ShaderLabPass pass = new ShaderLabPass();
                foreach (ShaderLabParsedTag tag in parsedPass.Tags)
                {
                    pass.Tags[tag.Key] = tag.Value;
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

                if (!string.IsNullOrWhiteSpace(parsedPass.HlslPlaceholder))
                {
                    if (!preprocessed.TryGetHlslBlock(parsedPass.HlslPlaceholder, out string? programSource))
                    {
                        throw new FormatException($"ShaderLab placeholder '{parsedPass.HlslPlaceholder}' has no extracted HLSL block.");
                    }

                    pass.Program = ParseProgram(programSource ?? string.Empty);
                }

                if (attachmentBlock is not null)
                {
                    pass.Program.AttachmentPhase =
                        ShaderLabAttachmentInterfaceParser.Parse(attachmentBlock);
                }

                pass.State = ParseRenderState(parsedPass.StateSource, parsedPass.StencilBlockContent);
                shaderLab.Passes.Add(pass);
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

            return shaderLab;
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

        internal static List<ShaderLabProperty> ParseShaderLabPropertiesBlock(string propertiesContent)
        {
            List<ShaderLabProperty> properties = new List<ShaderLabProperty>();
            if (string.IsNullOrWhiteSpace(propertiesContent))
            {
                return properties;
            }

            List<string> pendingAttributes = new List<string>();
            string[] lines = propertiesContent.Split('\n');
            foreach (string rawLine in lines)
            {
                string line = RemoveInlineComment(rawLine).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
                {
                    pendingAttributes.Add(line);
                    continue;
                }

                if (!TryParsePropertyLine(line, pendingAttributes, out ShaderLabProperty property))
                {
                    continue;
                }

                properties.Add(property);
                pendingAttributes = new List<string>();
            }

            return properties;
        }

        private static ShaderLabProgram ParseProgram(string programSource)
        {
            ShaderLabProgram program = new ShaderLabProgram
            {
                Source = programSource.Trim(),
                Entries = ParseProgramEntries(programSource),
            };

            program.KeywordGroups = ParseKeywordGroups(programSource);

            return program;
        }

        private static List<ShaderLabProgramEntry> ParseProgramEntries(string programSource)
        {
            List<ShaderLabProgramEntry> entries = new List<ShaderLabProgramEntry>();
            string pragmaSource = StripCommentsPreserveNewlines(programSource);
            foreach (Match match in s_PragmaRegex.Matches(pragmaSource))
            {
                string stageKeyword = match.Groups["stage"].Value;
                string entryName = match.Groups["entry"].Value;
                EShaderLabShaderStage stage = ParseStage(stageKeyword);
                if (stage == EShaderLabShaderStage.Undefined)
                {
                    continue;
                }

                entries.Add(new ShaderLabProgramEntry
                {
                    Stage = stage,
                    EntryName = entryName,
                });
            }

            return entries;
        }

        private static StandaloneShaderProgram ParseStandaloneProgram(string source, string sourcePath, StandaloneShaderProgramKind kind)
        {
            string normalizedSource = NormalizeSource(source);
            StandaloneShaderProgram program = new StandaloneShaderProgram
            {
                Kind = kind,
                SourcePath = sourcePath,
                Source = normalizedSource.Trim(),
            };

            program.Entries = ParseStandaloneEntries(normalizedSource, kind);
            program.KeywordGroups = ParseKeywordGroups(normalizedSource);

            return program;
        }

        private static List<StandaloneShaderEntry> ParseStandaloneEntries(string source, StandaloneShaderProgramKind kind)
        {
            List<StandaloneShaderEntry> entries = new List<StandaloneShaderEntry>();
            string pragmaSource = StripCommentsPreserveNewlines(source);
            foreach (Match match in s_StandalonePragmaRegex.Matches(pragmaSource))
            {
                string directive = match.Groups["directive"].Value.Trim().ToLowerInvariant();
                string args = match.Groups["args"].Value.Trim();
                if (string.IsNullOrWhiteSpace(args))
                {
                    continue;
                }

                string[] tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    continue;
                }

                if (kind == StandaloneShaderProgramKind.Compute)
                {
                    if (!directive.Equals("kernel", StringComparison.Ordinal) && !directive.Equals("compute", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    entries.Add(new StandaloneShaderEntry
                    {
                        Stage = StandaloneShaderStage.Compute,
                        EntryName = tokens[0],
                    });
                    continue;
                }

                if (kind == StandaloneShaderProgramKind.RayTrace)
                {
                    if (directive.Equals("raygeneration", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry
                        {
                            Stage = StandaloneShaderStage.RayGeneration,
                            EntryName = tokens[0],
                        });
                    }
                    else if (directive.Equals("miss", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry
                        {
                            Stage = StandaloneShaderStage.Miss,
                            EntryName = tokens[0],
                        });
                    }
                    else if (directive.Equals("callable", StringComparison.Ordinal))
                    {
                        entries.Add(new StandaloneShaderEntry
                        {
                            Stage = StandaloneShaderStage.Callable,
                            EntryName = tokens[0],
                        });
                    }
                }
            }

            return entries;
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
                    continue;
                }

                string[] tokens = args.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (tokens.Length == 0)
                {
                    continue;
                }

                ShaderKeywordGroup group = new ShaderKeywordGroup();
                for (int i = 0; i < tokens.Length; ++i)
                {
                    group.Keywords.Add(tokens[i]);
                }
                groups.Add(group);
            }

            return groups;
        }

        private static ShaderLabRenderState? ParseRenderState(string stateSource, string stencilSource)
        {
            ShaderLabRenderState state = new ShaderLabRenderState();
            bool hasState = false;

            Match cullMatch = Regex.Match(stateSource, @"\bCull\s+(Off|FrontAndBack|Front|Back)\b", RegexOptions.IgnoreCase);
            if (cullMatch.Success)
            {
                state.Cull = (int)ParseCullMode(cullMatch.Groups[1].Value);
                hasState = true;
            }

            Match zWriteMatch = Regex.Match(stateSource, @"\bZWrite\s+(On|Off)\b", RegexOptions.IgnoreCase);
            if (zWriteMatch.Success)
            {
                state.ZWrite = zWriteMatch.Groups[1].Value.Equals("On", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                hasState = true;
            }

            Match zTestMatch = Regex.Match(stateSource, @"\bZTest\s+(Disabled|Never|Less|Equal|LEqual|Greater|NotEqual|GEqual|Always)\b", RegexOptions.IgnoreCase);
            if (zTestMatch.Success)
            {
                state.ZTest = (int)ParseCompareFunction(zTestMatch.Groups[1].Value);
                hasState = true;
            }

            Match colorMaskMatch = Regex.Match(stateSource, @"\bColorMask\s+([A-Za-z0-9]+)\b", RegexOptions.IgnoreCase);
            if (colorMaskMatch.Success)
            {
                state.ColorMask = new ShaderLabFloatProperty(ParseColorMask(colorMaskMatch.Groups[1].Value), "ColorMask");
                hasState = true;
            }

            Match alphaToMaskMatch = Regex.Match(stateSource, @"\bAlphaToMask\s+(On|Off)\b", RegexOptions.IgnoreCase);
            if (alphaToMaskMatch.Success)
            {
                state.AlphaToMask = new ShaderLabFloatProperty(alphaToMaskMatch.Groups[1].Value.Equals("On", StringComparison.OrdinalIgnoreCase) ? 1f : 0f, "AlphaToMask");
                hasState = true;
            }

            Match offsetMatch = Regex.Match(stateSource, @"\bOffset\s+([-+]?\d*\.?\d+)\s*,?\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase);
            if (offsetMatch.Success)
            {
                state.OffsetFactor = new ShaderLabFloatProperty(ParseFloat(offsetMatch.Groups[1].Value), "OffsetFactor");
                state.OffsetUnits = new ShaderLabFloatProperty(ParseFloat(offsetMatch.Groups[2].Value), "OffsetUnits");
                hasState = true;
            }

            Match blendOpMatch = Regex.Match(stateSource, @"\bBlendOp\s+([A-Za-z]+)(?:\s*,\s*([A-Za-z]+))?", RegexOptions.IgnoreCase);
            if (blendOpMatch.Success)
            {
                state.BlendOp = new ShaderLabFloatProperty((int)ParseBlendOp(blendOpMatch.Groups[1].Value), "BlendOp");
                if (blendOpMatch.Groups[2].Success)
                {
                    state.BlendOpAlpha = new ShaderLabFloatProperty((int)ParseBlendOp(blendOpMatch.Groups[2].Value), "BlendOpAlpha");
                }

                hasState = true;
            }

            Match blendMatch = Regex.Match(stateSource, @"\bBlend\s+([A-Za-z]+)\s+([A-Za-z]+)(?:\s*,\s*([A-Za-z]+)\s+([A-Za-z]+))?", RegexOptions.IgnoreCase);
            if (blendMatch.Success)
            {
                state.SrcBlend = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[1].Value), "SrcBlend");
                state.DstBlend = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[2].Value), "DstBlend");
                if (blendMatch.Groups[3].Success && blendMatch.Groups[4].Success)
                {
                    state.SrcBlendAlpha = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[3].Value), "SrcBlendAlpha");
                    state.DstBlendAlpha = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[4].Value), "DstBlendAlpha");
                }
                else
                {
                    state.SrcBlendAlpha = state.SrcBlend;
                    state.DstBlendAlpha = state.DstBlend;
                }

                hasState = true;
            }

            if (!string.IsNullOrWhiteSpace(stencilSource))
            {
                ParseStencilState(stencilSource, state);
                hasState = true;
            }

            return hasState ? state : null;
        }

        private static void ParseStencilState(string stencilSource, ShaderLabRenderState state)
        {
            Match refMatch = Regex.Match(stencilSource, @"\bRef\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (refMatch.Success)
            {
                state.StencilRef = new ShaderLabFloatProperty(ParseFloat(refMatch.Groups[1].Value), "StencilRef");
            }

            Match readMaskMatch = Regex.Match(stencilSource, @"\bReadMask\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (readMaskMatch.Success)
            {
                state.StencilReadMask = new ShaderLabFloatProperty(ParseFloat(readMaskMatch.Groups[1].Value), "StencilReadMask");
            }

            Match writeMaskMatch = Regex.Match(stencilSource, @"\bWriteMask\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (writeMaskMatch.Success)
            {
                state.StencilWriteMask = new ShaderLabFloatProperty(ParseFloat(writeMaskMatch.Groups[1].Value), "StencilWriteMask");
            }

            ShaderLabStencilOp frontBack = new ShaderLabStencilOp();
            bool hasFrontBack = false;

            if (TryParseStencilOpValue(stencilSource, "Comp", out ShaderLabFloatProperty comp))
            {
                frontBack.Comp = comp;
                hasFrontBack = true;
            }
            if (TryParseStencilOpValue(stencilSource, "Pass", out ShaderLabFloatProperty pass))
            {
                frontBack.Pass = pass;
                hasFrontBack = true;
            }
            if (TryParseStencilOpValue(stencilSource, "Fail", out ShaderLabFloatProperty fail))
            {
                frontBack.Fail = fail;
                hasFrontBack = true;
            }
            if (TryParseStencilOpValue(stencilSource, "ZFail", out ShaderLabFloatProperty zFail))
            {
                frontBack.ZFail = zFail;
                hasFrontBack = true;
            }

            if (hasFrontBack)
            {
                state.StencilOp = frontBack;
            }

            ShaderLabStencilOp frontOnly = new ShaderLabStencilOp();
            bool hasFrontOnly = false;
            if (TryParseStencilOpValue(stencilSource, "CompFront", out ShaderLabFloatProperty compFront))
            {
                frontOnly.Comp = compFront;
                hasFrontOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "PassFront", out ShaderLabFloatProperty passFront))
            {
                frontOnly.Pass = passFront;
                hasFrontOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "FailFront", out ShaderLabFloatProperty failFront))
            {
                frontOnly.Fail = failFront;
                hasFrontOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "ZFailFront", out ShaderLabFloatProperty zFailFront))
            {
                frontOnly.ZFail = zFailFront;
                hasFrontOnly = true;
            }
            if (hasFrontOnly)
            {
                state.StencilOpFront = frontOnly;
            }

            ShaderLabStencilOp backOnly = new ShaderLabStencilOp();
            bool hasBackOnly = false;
            if (TryParseStencilOpValue(stencilSource, "CompBack", out ShaderLabFloatProperty compBack))
            {
                backOnly.Comp = compBack;
                hasBackOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "PassBack", out ShaderLabFloatProperty passBack))
            {
                backOnly.Pass = passBack;
                hasBackOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "FailBack", out ShaderLabFloatProperty failBack))
            {
                backOnly.Fail = failBack;
                hasBackOnly = true;
            }
            if (TryParseStencilOpValue(stencilSource, "ZFailBack", out ShaderLabFloatProperty zFailBack))
            {
                backOnly.ZFail = zFailBack;
                hasBackOnly = true;
            }
            if (hasBackOnly)
            {
                state.StencilOpBack = backOnly;
            }
        }

        private static bool TryParseStencilOpValue(string source, string key, out ShaderLabFloatProperty result)
        {
            result = default;
            Match match = Regex.Match(source, $@"\b{Regex.Escape(key)}\s+([A-Za-z]+)\b", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                return false;
            }

            if (key.StartsWith("Comp", StringComparison.OrdinalIgnoreCase))
            {
                result = new ShaderLabFloatProperty((int)ParseCompareFunction(match.Groups[1].Value), key);
                return true;
            }

            result = new ShaderLabFloatProperty((int)ParseStencilOp(match.Groups[1].Value), key);
            return true;
        }

        private static bool TryParsePropertyLine(string line, List<string> pendingAttributes, out ShaderLabProperty property)
        {
            property = new ShaderLabProperty();
            int propertyNameEnd = line.IndexOf('(');
            if (propertyNameEnd <= 0)
            {
                return false;
            }

            string propertyName = line.Substring(0, propertyNameEnd).Trim();
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            int declarationOpen = line.IndexOf('(', propertyNameEnd);
            if (declarationOpen < 0)
            {
                return false;
            }

            int declarationClose = FindMatchingBracket(line, declarationOpen, '(', ')');
            if (declarationClose < 0)
            {
                return false;
            }

            string declaration = line.Substring(declarationOpen + 1, declarationClose - declarationOpen - 1);
            if (!TryParsePropertyDeclaration(declaration, out string displayName, out string typeToken))
            {
                return false;
            }

            int equalsIndex = line.IndexOf('=', declarationClose + 1);
            if (equalsIndex < 0)
            {
                return false;
            }

            string defaultValue = line.Substring(equalsIndex + 1).Trim();
            List<string>? attributes = pendingAttributes.Count > 0 ? new List<string>(pendingAttributes) : null;

            ShaderLabProperty parsed = ParsePropertyValue(propertyName, displayName, typeToken, defaultValue, attributes);
            property = parsed;
            return true;
        }

        private static bool TryParsePropertyDeclaration(string declaration, out string displayName, out string typeToken)
        {
            displayName = string.Empty;
            typeToken = string.Empty;

            int firstQuote = declaration.IndexOf('"');
            if (firstQuote < 0)
            {
                return false;
            }

            int secondQuote = declaration.IndexOf('"', firstQuote + 1);
            if (secondQuote <= firstQuote)
            {
                return false;
            }

            displayName = declaration.Substring(firstQuote + 1, secondQuote - firstQuote - 1);
            int commaIndex = declaration.IndexOf(',', secondQuote + 1);
            if (commaIndex < 0)
            {
                return false;
            }

            typeToken = declaration.Substring(commaIndex + 1).Trim();
            return !string.IsNullOrWhiteSpace(typeToken);
        }

        private static ShaderLabProperty ParsePropertyValue(
            string propertyName,
            string displayName,
            string typeToken,
            string defaultValue,
            List<string>? attributes)
        {
            string normalizedType = typeToken.Trim();
            string upperType = normalizedType.ToUpperInvariant();

            if (upperType.StartsWith("RANGE", StringComparison.Ordinal))
            {
                (float minValue, float maxValue) = ParseRangeBounds(normalizedType);
                float scalar = ParseFloat(defaultValue);
                return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Range, new System.Numerics.Vector4(scalar, minValue, maxValue, 0))
                {
                    RangeMin = minValue,
                    RangeMax = maxValue,
                };
            }

            if (upperType.Equals("INT", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Int, new System.Numerics.Vector4(ParseFloat(defaultValue), 0, 0, 0));
            }

            if (upperType.Equals("FLOAT", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Float, new System.Numerics.Vector4(ParseFloat(defaultValue), 0, 0, 0));
            }

            if (upperType.Equals("COLOR", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Color, ParseVector(defaultValue));
            }

            if (upperType.Equals("VECTOR", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Vector, ParseVector(defaultValue));
            }

            EShaderLabTextureDimension dimension = ParseTextureDimension(normalizedType);
            if (dimension != EShaderLabTextureDimension.Undefined)
            {
                string textureDefault = ParseTextureDefaultValue(defaultValue);
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Texture,
                    new ShaderLabTextureProperty(propertyName, dimension, textureDefault));
            }

            return new ShaderLabProperty(displayName, propertyName, attributes, EShaderLabPropertyType.Undefined, new System.Numerics.Vector4(0, 0, 0, 0));
        }

        private static EShaderLabTextureDimension ParseTextureDimension(string typeToken)
        {
            string token = typeToken.Trim().ToUpperInvariant();
            return token switch
            {
                "2D" => EShaderLabTextureDimension.Tex2D,
                "2DARRAY" => EShaderLabTextureDimension.Tex2DArray,
                "3D" => EShaderLabTextureDimension.Tex3D,
                "CUBE" => EShaderLabTextureDimension.TexCube,
                _ => EShaderLabTextureDimension.Undefined,
            };
        }

        private static string ParseTextureDefaultValue(string defaultValue)
        {
            Match quoted = Regex.Match(defaultValue, "\"(?<name>.*?)\"");
            if (quoted.Success)
            {
                return quoted.Groups["name"].Value;
            }

            return "white";
        }

        private static EShaderLabShaderStage ParseStage(string stageKeyword)
        {
            string token = stageKeyword.Trim().ToLowerInvariant();
            return token switch
            {
                "vertex" => EShaderLabShaderStage.ProgramVertex,
                "fragment" => EShaderLabShaderStage.ProgramFragment,
                "mesh" => EShaderLabShaderStage.ProgramMesh,
                "task" => EShaderLabShaderStage.ProgramTask,
                "compute" => EShaderLabShaderStage.ProgramCompute,
                "raygeneration" => EShaderLabShaderStage.ProgramRayGen,
                "intersection" => EShaderLabShaderStage.ProgramRayInt,
                "anyhit" => EShaderLabShaderStage.ProgramRayAHit,
                "closesthit" => EShaderLabShaderStage.ProgramRayCHit,
                "miss" => EShaderLabShaderStage.ProgramRayMiss,
                "callable" => EShaderLabShaderStage.ProgramRayRcall,
                _ => EShaderLabShaderStage.Undefined,
            };
        }

        private static EShaderLabCullMode ParseCullMode(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "off" => EShaderLabCullMode.CullOff,
                "front" => EShaderLabCullMode.CullFront,
                "back" => EShaderLabCullMode.CullBack,
                "frontandback" => EShaderLabCullMode.CullFrontAndBack,
                _ => EShaderLabCullMode.Undefined,
            };
        }

        private static EShaderLabCompareFunction ParseCompareFunction(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "disabled" => EShaderLabCompareFunction.FuncDisabled,
                "never" => EShaderLabCompareFunction.FuncNever,
                "less" => EShaderLabCompareFunction.FuncLess,
                "equal" => EShaderLabCompareFunction.FuncEqual,
                "lequal" => EShaderLabCompareFunction.FuncLEqual,
                "greater" => EShaderLabCompareFunction.FuncGreater,
                "notequal" => EShaderLabCompareFunction.FuncNotEqual,
                "gequal" => EShaderLabCompareFunction.FuncGEqual,
                "always" => EShaderLabCompareFunction.FuncAlways,
                _ => EShaderLabCompareFunction.Undefined,
            };
        }

        private static EShaderLabBlendMode ParseBlendMode(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "zero" => EShaderLabBlendMode.BlendZero,
                "one" => EShaderLabBlendMode.BlendOne,
                "dstcolor" => EShaderLabBlendMode.BlendDstColor,
                "srccolor" => EShaderLabBlendMode.BlendSrcColor,
                "oneminusdstcolor" => EShaderLabBlendMode.BlendOneMinusDstColor,
                "srcalpha" => EShaderLabBlendMode.BlendSrcAlpha,
                "oneminussrccolor" => EShaderLabBlendMode.BlendOneMinusSrcColor,
                "dstalpha" => EShaderLabBlendMode.BlendDstAlpha,
                "oneminusdstalpha" => EShaderLabBlendMode.BlendOneMinusDstAlpha,
                "srcalphasaturate" => EShaderLabBlendMode.BlendSrcAlphaSaturate,
                "oneminussrcalpha" => EShaderLabBlendMode.BlendOneMinusSrcAlpha,
                _ => EShaderLabBlendMode.Undefined,
            };
        }

        private static EShaderLabBlendOp ParseBlendOp(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "add" => EShaderLabBlendOp.BlendOpAdd,
                "sub" or "subtract" => EShaderLabBlendOp.BlendOpSub,
                "revsub" or "reversesubtract" => EShaderLabBlendOp.BlendOpRevSub,
                "min" => EShaderLabBlendOp.BlendOpMin,
                "max" => EShaderLabBlendOp.BlendOpMax,
                "logicalclear" => EShaderLabBlendOp.BlendOpLogicalClear,
                "logicalset" => EShaderLabBlendOp.BlendOpLogicalSet,
                "logicalcopy" => EShaderLabBlendOp.BlendOpLogicalCopy,
                "logicalcopyinverted" => EShaderLabBlendOp.BlendOpLogicalCopyInverted,
                "logicalnoop" => EShaderLabBlendOp.BlendOpLogicalNoop,
                "logicalinvert" => EShaderLabBlendOp.BlendOpLogicalInvert,
                "logicaland" => EShaderLabBlendOp.BlendOpLogicalAnd,
                "logicalnand" => EShaderLabBlendOp.BlendOpLogicalNand,
                "logicalor" => EShaderLabBlendOp.BlendOpLogicalOr,
                "logicalnor" => EShaderLabBlendOp.BlendOpLogicalNor,
                "logicalxor" => EShaderLabBlendOp.BlendOpLogicalXor,
                "logicalequiv" => EShaderLabBlendOp.BlendOpLogicalEquiv,
                "logicalandreverse" => EShaderLabBlendOp.BlendOpLogicalAndReverse,
                "logicalandinverted" => EShaderLabBlendOp.BlendOpLogicalAndInverted,
                "logicalorreverse" => EShaderLabBlendOp.BlendOpLogicalOrReverse,
                "logicalorinverted" => EShaderLabBlendOp.BlendOpLogicalOrInverted,
                _ => EShaderLabBlendOp.Undefined,
            };
        }

        private static EShaderLabStencilOp ParseStencilOp(string token)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "keep" => EShaderLabStencilOp.StencilOpKeep,
                "zero" => EShaderLabStencilOp.StencilOpZero,
                "replace" => EShaderLabStencilOp.StencilOpReplace,
                "incrsat" => EShaderLabStencilOp.StencilOpIncrSat,
                "decrsat" => EShaderLabStencilOp.StencilOpDecrSat,
                "invert" => EShaderLabStencilOp.StencilOpInvert,
                "incrwrap" or "incr" => EShaderLabStencilOp.StencilOpIncrWrap,
                "decrwrap" or "decr" => EShaderLabStencilOp.StencilOpDecrWrap,
                _ => EShaderLabStencilOp.Undefined,
            };
        }

        private static float ParseColorMask(string token)
        {
            string upper = token.Trim().ToUpperInvariant();
            if (int.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericMask))
            {
                return numericMask;
            }

            int mask = 0;
            if (upper.Contains('R', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteR;
            }
            if (upper.Contains('G', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteG;
            }
            if (upper.Contains('B', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteB;
            }
            if (upper.Contains('A', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteA;
            }

            return mask;
        }

        private static float ParseFloat(string raw)
        {
            string normalized = raw.Trim().TrimEnd('f', 'F');
            return float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ? value : 0f;
        }

        private static System.Numerics.Vector4 ParseVector(string raw)
        {
            string text = raw.Trim();
            int open = text.IndexOf('(');
            int close = text.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                text = text.Substring(open + 1, close - open - 1);
            }

            string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            float x = parts.Length > 0 ? ParseFloat(parts[0]) : 0f;
            float y = parts.Length > 1 ? ParseFloat(parts[1]) : 0f;
            float z = parts.Length > 2 ? ParseFloat(parts[2]) : 0f;
            float w = parts.Length > 3 ? ParseFloat(parts[3]) : 0f;
            return new System.Numerics.Vector4(x, y, z, w);
        }

        private static (float Min, float Max) ParseRangeBounds(string rangeToken)
        {
            int open = rangeToken.IndexOf('(');
            int close = rangeToken.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                return (0f, 1f);
            }

            string range = rangeToken.Substring(open + 1, close - open - 1);
            string[] parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            float min = parts.Length > 0 ? ParseFloat(parts[0]) : 0f;
            float max = parts.Length > 1 ? ParseFloat(parts[1]) : 1f;
            return (min, max);
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
