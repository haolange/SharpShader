using System;
using Antlr4.Runtime;
using System.Collections.Generic;
using SharpShader.ShaderLab.Frontend.Generated;

namespace SharpShader.ShaderLab.Frontend
{
    internal sealed class ShaderLabDslParseResult
    {
        public string Name { get; set; } = string.Empty;
        public string PropertiesBlockContent { get; set; } = string.Empty;
        public List<ShaderLabParsedTag> ShaderTags { get; } = new List<ShaderLabParsedTag>();
        public List<ShaderLabParsedPass> Passes { get; } = new List<ShaderLabParsedPass>();
    }

    internal sealed class ShaderLabParsedPass
    {
        public List<ShaderLabParsedTag> Tags { get; } = new List<ShaderLabParsedTag>();
        public string StateSource { get; set; } = string.Empty;
        public string StencilBlockContent { get; set; } = string.Empty;
        public string HlslPlaceholder { get; set; } = string.Empty;
    }

    internal readonly struct ShaderLabParsedTag
    {
        public string Key { get; }
        public string Value { get; }
        public int Line { get; }
        public int Column { get; }

        public ShaderLabParsedTag(string key, string value, int line, int column)
        {
            Key = key;
            Value = value;
            Line = line;
            Column = column;
        }
    }

    internal static class ShaderLabDslParser
    {
        public static ShaderLabDslParseResult Parse(string sanitizedSource)
        {
            AntlrInputStream input = new AntlrInputStream(sanitizedSource);
            ShaderLabLexer lexer = new ShaderLabLexer(input);
            lexer.RemoveErrorListeners();
            lexer.AddErrorListener(ShaderLabLexerErrorListener.Instance);

            CommonTokenStream tokenStream = new CommonTokenStream(lexer);
            tokenStream.Fill();

            EnforceCategoryForbidden(tokenStream.GetTokens());

            ShaderLabParser parser = new ShaderLabParser(tokenStream);
            parser.RemoveErrorListeners();
            parser.AddErrorListener(ShaderLabParserErrorListener.Instance);

            ShaderLabParser.ShaderFileContext fileContext = parser.shaderFile();
            ShaderLabParser.ShaderDeclContext shaderContext = fileContext.shaderDecl();

            ShaderLabDslParseResult result = new ShaderLabDslParseResult
            {
                Name = Unquote(shaderContext.STRING().GetText()),
            };

            ShaderLabParser.ShaderBodyContext body = shaderContext.shaderBody();
            if (body.propertiesSection() != null)
            {
                result.PropertiesBlockContent = ExtractInnerBlockText(sanitizedSource, body.propertiesSection().genericBlock());
            }

            if (body.shaderTagsSection() != null)
            {
                AppendTagPairs(result.ShaderTags, body.shaderTagsSection().tagsBlock());
            }

            foreach (ShaderLabParser.PassSectionContext passSection in body.passSection())
            {
                result.Passes.Add(ParsePass(sanitizedSource, passSection.passBlock()));
            }

            return result;
        }

        private static void EnforceCategoryForbidden(IList<IToken> tokens)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                IToken token = tokens[i];
                if (token.Type == ShaderLabLexer.CATEGORY)
                {
                    throw new ShaderLabParseException(
                        token.Line,
                        token.Column + 1,
                        "Category 不被支持，改用 shader 根级 Tags + Pass。");
                }
            }
        }

        private static ShaderLabParsedPass ParsePass(string source, ShaderLabParser.PassBlockContext passBlock)
        {
            ShaderLabParsedPass pass = new ShaderLabParsedPass();
            List<string> stateSegments = new List<string>();

            foreach (ShaderLabParser.PassElementContext element in passBlock.passElement())
            {
                if (element.passTagsSection() != null)
                {
                    List<ShaderLabParsedTag> parsedTags = new List<ShaderLabParsedTag>();
                    AppendTagPairs(parsedTags, element.passTagsSection().tagsBlock());
                    for (int i = 0; i < parsedTags.Count; i++)
                    {
                        ShaderLabParsedTag tag = parsedTags[i];
                        if (string.Equals(tag.Key, "RenderType", StringComparison.Ordinal))
                        {
                            throw new ShaderLabParseException(
                                tag.Line,
                                tag.Column,
                                "RenderType 只能出现在 shader 根级 Tags，不能出现在 Pass 的 Tags 中。");
                        }

                        pass.Tags.Add(tag);
                    }

                    continue;
                }

                if (element.stencilSection() != null)
                {
                    pass.StencilBlockContent = ExtractInnerBlockText(source, element.stencilSection().genericBlock());
                    continue;
                }

                if (element.hlslProgramSection() != null)
                {
                    if (!string.IsNullOrEmpty(pass.HlslPlaceholder))
                    {
                        IToken duplicated = element.hlslProgramSection().HLSLPROGRAM().Symbol;
                        throw new ShaderLabParseException(
                            duplicated.Line,
                            duplicated.Column + 1,
                            "同一个 Pass 只允许一个 HLSLPROGRAM...ENDHLSL 块。");
                    }

                    pass.HlslPlaceholder = element.hlslProgramSection().HLSL_PLACEHOLDER().GetText();
                    continue;
                }

                if (element.stateStatement() != null)
                {
                    stateSegments.Add(ExtractText(source, element.stateStatement()));
                }
            }

            pass.StateSource = string.Join("\n", stateSegments);
            return pass;
        }

        private static void AppendTagPairs(List<ShaderLabParsedTag> destination, ShaderLabParser.TagsBlockContext tagsBlock)
        {
            foreach (ShaderLabParser.TagPairContext pair in tagsBlock.tagPair())
            {
                IToken keyToken = pair.STRING(0).Symbol;
                string key = Unquote(pair.STRING(0).GetText());
                string value = Unquote(pair.STRING(1).GetText());
                destination.Add(new ShaderLabParsedTag(key, value, keyToken.Line, keyToken.Column + 1));
            }
        }

        private static string ExtractText(string source, ParserRuleContext context)
        {
            if (context.Start == null || context.Stop == null)
            {
                return string.Empty;
            }

            int start = context.Start.StartIndex;
            int stop = context.Stop.StopIndex;
            if (start < 0 || stop < start || stop >= source.Length)
            {
                return string.Empty;
            }

            return source.Substring(start, stop - start + 1);
        }

        private static string ExtractInnerBlockText(string source, ShaderLabParser.GenericBlockContext blockContext)
        {
            if (blockContext == null || blockContext.Start == null || blockContext.Stop == null)
            {
                return string.Empty;
            }

            int start = blockContext.Start.StartIndex + 1;
            int stop = blockContext.Stop.StopIndex - 1;
            if (start < 0 || stop < start || stop >= source.Length)
            {
                return string.Empty;
            }

            return source.Substring(start, stop - start + 1);
        }

        private static string Unquote(string tokenText)
        {
            if (string.IsNullOrEmpty(tokenText) || tokenText.Length < 2)
            {
                return tokenText;
            }

            if (tokenText[0] != '"' || tokenText[tokenText.Length - 1] != '"')
            {
                return tokenText;
            }

            string inner = tokenText.Substring(1, tokenText.Length - 2);
            return inner
                .Replace("\\\"", "\"", StringComparison.Ordinal)
                .Replace("\\\\", "\\", StringComparison.Ordinal);
        }
    }
}
