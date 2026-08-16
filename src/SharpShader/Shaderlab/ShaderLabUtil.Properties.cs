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
        internal static List<ShaderLabProperty> ParseShaderLabPropertiesBlock(string propertiesContent)
        {
            List<ShaderLabProperty> properties = new List<ShaderLabProperty>();
            if (string.IsNullOrWhiteSpace(propertiesContent))
            {
                return properties;
            }

            List<string> pendingAttributes = new List<string>();
            string[] lines = propertiesContent.Split('\n');
            for (int lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
            {
                int lineNumber = lineIndex + 1;
                string line = RemoveInlineComment(lines[lineIndex]).Trim();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    pendingAttributes.Add(line);
                    continue;
                }

                properties.Add(ParsePropertyLine(line, pendingAttributes, lineNumber));
                pendingAttributes = new List<string>();
            }

            if (pendingAttributes.Count > 0)
            {
                throw ParseError(
                    lines.Length,
                    1,
                    "ShaderLab property attributes must be followed by a property declaration.");
            }

            return properties;
        }

        private static ShaderLabProperty ParsePropertyLine(string line, List<string> pendingAttributes, int lineNumber)
        {
            int propertyNameEnd = line.IndexOf('(');
            if (propertyNameEnd <= 0)
            {
                throw ParseError(lineNumber, 1, $"Invalid ShaderLab property line '{line}'.");
            }

            string propertyName = line.Substring(0, propertyNameEnd).Trim();
            if (string.IsNullOrWhiteSpace(propertyName))
            {
                throw ParseError(lineNumber, 1, "ShaderLab property name is empty.");
            }

            int declarationOpen = line.IndexOf('(', propertyNameEnd);
            if (declarationOpen < 0)
            {
                throw ParseError(lineNumber, 1, $"ShaderLab property '{propertyName}' is missing a type declaration.");
            }

            int declarationClose = FindMatchingBracket(line, declarationOpen, '(', ')');
            if (declarationClose < 0)
            {
                throw ParseError(lineNumber, declarationOpen + 1, $"ShaderLab property '{propertyName}' has an unclosed type declaration.");
            }

            string declaration = line.Substring(declarationOpen + 1, declarationClose - declarationOpen - 1);
            if (!TryParsePropertyDeclaration(declaration, out string displayName, out string typeToken))
            {
                throw ParseError(lineNumber, declarationOpen + 1, $"ShaderLab property '{propertyName}' has an invalid type declaration.");
            }

            int equalsIndex = line.IndexOf('=', declarationClose + 1);
            if (equalsIndex < 0)
            {
                throw ParseError(lineNumber, declarationClose + 1, $"ShaderLab property '{propertyName}' is missing a default value.");
            }

            string defaultValue = line.Substring(equalsIndex + 1).Trim();
            IReadOnlyList<string>? attributes = pendingAttributes.Count > 0 ? pendingAttributes.ToArray() : null;
            return ParsePropertyValue(propertyName, displayName, typeToken, defaultValue, attributes, lineNumber);
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
            IReadOnlyList<string>? attributes,
            int lineNumber)
        {
            string normalizedType = typeToken.Trim();
            string upperType = normalizedType.ToUpperInvariant();

            if (upperType.StartsWith("RANGE", StringComparison.Ordinal))
            {
                (float minValue, float maxValue) = ParseRangeBounds(normalizedType, lineNumber);
                float scalar = ParseFloat(defaultValue, lineNumber, 1);
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Range,
                    new System.Numerics.Vector4(scalar, minValue, maxValue, 0),
                    minValue,
                    maxValue);
            }

            if (upperType.Equals("INT", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Int,
                    new System.Numerics.Vector4(ParseFloat(defaultValue, lineNumber, 1), 0, 0, 0));
            }

            if (upperType.Equals("FLOAT", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Float,
                    new System.Numerics.Vector4(ParseFloat(defaultValue, lineNumber, 1), 0, 0, 0));
            }

            if (upperType.Equals("COLOR", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Color,
                    ParseVector(defaultValue, lineNumber, 1));
            }

            if (upperType.Equals("VECTOR", StringComparison.Ordinal))
            {
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Vector,
                    ParseVector(defaultValue, lineNumber, 1));
            }

            EShaderLabTextureDimension dimension = ParseTextureDimension(normalizedType);
            if (dimension != EShaderLabTextureDimension.Undefined)
            {
                string textureDefault = ParseTextureDefaultValue(defaultValue, lineNumber);
                return new ShaderLabProperty(
                    displayName,
                    propertyName,
                    attributes,
                    EShaderLabPropertyType.Texture,
                    new ShaderLabTextureProperty(propertyName, dimension, textureDefault));
            }

            throw ParseError(
                lineNumber,
                1,
                $"Unknown ShaderLab property type '{typeToken}' for '{propertyName}'.");
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

        private static string ParseTextureDefaultValue(string defaultValue, int lineNumber)
        {
            Match quoted = Regex.Match(defaultValue, "\"(?<name>.*?)\"");
            if (quoted.Success)
            {
                return quoted.Groups["name"].Value;
            }

            if (string.IsNullOrWhiteSpace(defaultValue))
            {
                throw ParseError(lineNumber, 1, "Texture property default value is empty.");
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

        private static EShaderLabCullMode ParseCullMode(string token, int line, int column)
        {
            string lower = token.Trim().ToLowerInvariant();
            return lower switch
            {
                "off" => EShaderLabCullMode.CullOff,
                "front" => EShaderLabCullMode.CullFront,
                "back" => EShaderLabCullMode.CullBack,
                "frontandback" => EShaderLabCullMode.CullFrontAndBack,
                _ => throw ParseError(line, column, $"Unknown Cull mode '{token}'."),
            };
        }

        private static EShaderLabCompareFunction ParseCompareFunction(string token, int line, int column)
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
                _ => throw ParseError(line, column, $"Unknown compare function '{token}'."),
            };
        }

        private static EShaderLabBlendMode ParseBlendMode(string token, int line, int column)
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
                _ => throw ParseError(line, column, $"Unknown blend mode '{token}'."),
            };
        }

        private static EShaderLabBlendOp ParseBlendOp(string token, int line, int column)
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
                _ => throw ParseError(line, column, $"Unknown blend op '{token}'."),
            };
        }

        private static EShaderLabStencilOp ParseStencilOp(string token, int line, int column)
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
                _ => throw ParseError(line, column, $"Unknown stencil op '{token}'."),
            };
        }

        private static float ParseColorMask(string token, int line, int column)
        {
            string upper = token.Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(upper))
            {
                throw ParseError(line, column, "ColorMask value is empty.");
            }

            if (int.TryParse(upper, NumberStyles.Integer, CultureInfo.InvariantCulture, out int numericMask))
            {
                return numericMask;
            }

            int mask = 0;
            bool hasChannel = false;
            if (upper.Contains('R', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteR;
                hasChannel = true;
            }
            if (upper.Contains('G', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteG;
                hasChannel = true;
            }
            if (upper.Contains('B', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteB;
                hasChannel = true;
            }
            if (upper.Contains('A', StringComparison.Ordinal))
            {
                mask |= (int)EShaderLabColorWriteMask.ColorWriteA;
                hasChannel = true;
            }

            if (!hasChannel)
            {
                throw ParseError(line, column, $"Invalid ColorMask '{token}'.");
            }

            return mask;
        }

        private static float ParseFloat(string raw, int line, int column)
        {
            string normalized = raw.Trim().TrimEnd('f', 'F');
            if (!float.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                throw ParseError(line, column, $"Invalid floating-point literal '{raw}'.");
            }

            return value;
        }

        private static System.Numerics.Vector4 ParseVector(string raw, int line, int column)
        {
            string text = raw.Trim();
            int open = text.IndexOf('(');
            int close = text.LastIndexOf(')');
            if (open >= 0 && close > open)
            {
                text = text.Substring(open + 1, close - open - 1);
            }

            string[] parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                throw ParseError(
                    line,
                    column,
                    $"Vector/Color default '{raw}' must contain exactly 4 components.");
            }

            return new System.Numerics.Vector4(
                ParseFloat(parts[0], line, column),
                ParseFloat(parts[1], line, column),
                ParseFloat(parts[2], line, column),
                ParseFloat(parts[3], line, column));
        }

        private static (float Min, float Max) ParseRangeBounds(string rangeToken, int line)
        {
            int open = rangeToken.IndexOf('(');
            int close = rangeToken.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                throw ParseError(line, 1, $"Range type '{rangeToken}' must specify (min, max) bounds.");
            }

            string range = rangeToken.Substring(open + 1, close - open - 1);
            string[] parts = range.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                throw ParseError(line, open + 1, $"Range type '{rangeToken}' must specify exactly two bounds.");
            }

            return (ParseFloat(parts[0], line, open + 1), ParseFloat(parts[1], line, open + 1));
        }
    }
}
