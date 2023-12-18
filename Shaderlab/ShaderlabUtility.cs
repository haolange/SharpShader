using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SharpShader.ShaderLab
{
    public static class ShaderLabUtil
    {
        public static ShaderLab ParseShaderLabFromFile(string filePath)
        {
            string source = File.ReadAllText(filePath);
            return ParseShaderLabFromSource(source);
        }

        public static ShaderLab ParseShaderLabFromSource(string source)
        {
            ShaderLab ShaderLab = new ShaderLab();
            ShaderLab.Name = ParseShaderLabName(source);
            ShaderLab.Category = ParseShaderLabCategory(source);
            ShaderLab.Properties = ParseShaderLabProperties(source);
            return ShaderLab;
        }

        internal static string ParseShaderLabName(string source)
        {
            Regex regex = new Regex(@"Shader "".+""");
            foreach (Match match in regex.Matches(source))
            {
                int count = match.Value.Length;
                return match.Value.Substring(8, count - 9);
            }
            throw new NotImplementedException("ShaderLab name is illegal");
        }

        internal static ShaderLabCategory ParseShaderLabCategory(string source)
        {
            string categoryPattern = @"(?<=\b(Category)\b)\s*{(?:[^{}]+|(?<open>{)|(?<-open>})|(?:(?<close-open>)[^{}]*)+)*(?(open)(?!))}";
            MatchCollection categoryMatches = Regex.Matches(source, categoryPattern, RegexOptions.Singleline);

            ShaderLabCategory ShaderLabCategory = new ShaderLabCategory(2);

            foreach (Match categoryMatche in categoryMatches)
            {
                string categoryBlock = categoryMatche.Value;

                //string tagsPattern = @"Tags\s*{[^}]*\}(?!Pass)";
                string tagsPattern = @"(?<=Category\s*\{\s*Tags\s*\{\s*)((?:(?:(?!Pass\s*\{)(?!\}\s*\}).)*\n?)*)\}\s*(?=(?:Pass\s*\{|}))";
                MatchCollection tagsMatches = Regex.Matches(source, tagsPattern, RegexOptions.Singleline);
                foreach (Match tagsMatche in tagsMatches)
                {
                    string tagsBlock = tagsMatche.Value;

                    // 使用正则表达式匹配键和值
                    string pattern = @"\""(.*?)\""\s*=\s*\""(.*?)\""(?=\s|}|$)";
                    MatchCollection matches = Regex.Matches(tagsBlock, pattern, RegexOptions.Singleline);

                    // 解析每个键值对
                    foreach (Match match in matches)
                    {
                        string key = match.Groups[1].Value;
                        string value = match.Groups[2].Value;
                        ShaderLabCategory.Tags.Add(key, value);
                    }
                }

                string passPattern = @"(?<=Pass\s*\{\s*)(?>[^{}]+|\{(?:[^{}]+|\{(?:[^{}]+|\{[^{}]*\})*\})*\})*(?=\s*\})";
                MatchCollection passMatches = Regex.Matches(categoryBlock, passPattern, RegexOptions.Singleline);
                foreach (Match passMatche in passMatches)
                {
                    string passBlock = passMatche.Value;
                    ShaderLabPass ShaderLabPass = new ShaderLabPass(2);

                    string passTagsPattern = @"(?<=Tags\s*{\s*)(?:(?!Pass\s*{)[^}])*(?=})";
                    MatchCollection passTagsMatches = Regex.Matches(passBlock, passTagsPattern, RegexOptions.Singleline);
                    foreach (Match passTagsMatche in passTagsMatches)
                    {
                        string passTagsBlock = passTagsMatche.Value;

                        // 使用正则表达式匹配键和值
                        string pattern = @"\""(.*?)\""\s*=\s*\""(.*?)\""(?=\s|$)";
                        MatchCollection matches = Regex.Matches(passTagsBlock, pattern, RegexOptions.Singleline);

                        // 解析每个键值对
                        foreach (Match match in matches)
                        {
                            string key = match.Groups[1].Value;
                            string value = match.Groups[2].Value;
                            ShaderLabPass.Tags.Add(key, value);
                        }
                    }

                    string programPattern = @"(?<=HLSLPROGRAM\s).*?(?=\s*ENDHLSL)";
                    MatchCollection programMatches = Regex.Matches(passBlock, programPattern, RegexOptions.Singleline);
                    foreach (Match programMatche in programMatches)
                    {
                        string programBlock = programMatche.Value;
                        ShaderLabPass.Program = new ShaderLabProgram
                        {
                            Source = programBlock,
                        };
                        
                    }
                    ShaderLabCategory.Passes.Add(ShaderLabPass);
                }
            }

            return ShaderLabCategory;
        }

        internal static List<ShaderLabProperty> ParseShaderLabProperties(string source)
        {
            string propertiesBlock;
            string pattern = @"(?<=\b(Properties)\b)\s*{(?:[^{}]+|(?<open>{)|(?<-open>})|(?:(?<close-open>)[^{}]*)+)*(?(open)(?!))}";
            MatchCollection matches = Regex.Matches(source, pattern, RegexOptions.Singleline);

            foreach (Match match in matches)
            {
                propertiesBlock = match.Value;
                propertiesBlock = propertiesBlock.Trim(); // 去除字符串前后的空白字符
                propertiesBlock = Regex.Replace(propertiesBlock, @"^\s+", "", RegexOptions.Multiline); // 删除每一行开头的缩进
                propertiesBlock = propertiesBlock.Remove(0, 1);
                propertiesBlock = propertiesBlock.Remove(propertiesBlock.Length - 1, 1);
            }

            return null;
        }
    }
}