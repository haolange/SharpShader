using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Infinity.Shaderlib
{
    public static class ShaderlabUtility
    {
        public static Shaderlab ParseShaderlabFromFile(string filePath)
        {
            string source = File.ReadAllText(filePath);
            return ParseShaderlabFromSource(source);
        }

        public static Shaderlab ParseShaderlabFromSource(string source)
        {
            Shaderlab shaderlab = new Shaderlab();
            shaderlab.Name = ParseShaderlabName(source);
            shaderlab.Category = ParseShaderlabCategory(source);
            shaderlab.Properties = ParseShaderlabProperties(source);
            return shaderlab;
        }

        internal static string ParseShaderlabName(string source)
        {
            Regex regex = new Regex(@"Shader "".+""");
            foreach (Match match in regex.Matches(source))
            {
                int count = match.Value.Length;
                return match.Value.Substring(8, count - 9);
            }
            throw new NotImplementedException("Shaderlab name is illegal");
        }

        internal static ShaderlabCategory ParseShaderlabCategory(string source)
        {
            string categoryPattern = @"(?<=\b(Category)\b)\s*{(?:[^{}]+|(?<open>{)|(?<-open>})|(?:(?<close-open>)[^{}]*)+)*(?(open)(?!))}";
            MatchCollection categoryMatches = Regex.Matches(source, categoryPattern, RegexOptions.Singleline);

            ShaderlabCategory shaderlabCategory = new ShaderlabCategory(2);

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
                        shaderlabCategory.Tags.Add(key, value);
                    }
                }

                string passPattern = @"(?<=Pass\s*\{\s*)(?>[^{}]+|\{(?:[^{}]+|\{(?:[^{}]+|\{[^{}]*\})*\})*\})*(?=\s*\})";
                MatchCollection passMatches = Regex.Matches(categoryBlock, passPattern, RegexOptions.Singleline);
                foreach (Match passMatche in passMatches)
                {
                    string passBlock = passMatche.Value;
                    ShaderlabPass shaderlabPass = new ShaderlabPass(2);

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
                            shaderlabPass.Tags.Add(key, value);
                        }
                    }

                    string programPattern = @"(?<=HLSLPROGRAM\s).*?(?=\s*ENDHLSL)";
                    MatchCollection programMatches = Regex.Matches(passBlock, programPattern, RegexOptions.Singleline);
                    foreach (Match programMatche in programMatches)
                    {
                        string programBlock = programMatche.Value;
                        shaderlabPass.Program = new ShaderlabProgram
                        {
                            Source = programBlock,
                        };
                        
                    }
                    shaderlabCategory.Passes.Add(shaderlabPass);
                }
            }

            return shaderlabCategory;
        }

        internal static List<ShaderlabProperty> ParseShaderlabProperties(string source)
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