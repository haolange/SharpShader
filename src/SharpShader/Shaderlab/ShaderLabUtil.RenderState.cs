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
        private static ShaderLabRenderState? ParseRenderState(string stateSource, string stencilSource)
        {
            int? cull = null;
            int? zWrite = null;
            int? zTest = null;
            ShaderLabFloatProperty? colorMask = null;
            ShaderLabFloatProperty? alphaToMask = null;
            ShaderLabFloatProperty? offsetFactor = null;
            ShaderLabFloatProperty? offsetUnits = null;
            ShaderLabFloatProperty? blendOp = null;
            ShaderLabFloatProperty? blendOpAlpha = null;
            ShaderLabFloatProperty? srcBlend = null;
            ShaderLabFloatProperty? dstBlend = null;
            ShaderLabFloatProperty? srcBlendAlpha = null;
            ShaderLabFloatProperty? dstBlendAlpha = null;
            bool hasState = false;

            Match cullMatch = Regex.Match(stateSource, @"\bCull\s+(Off|FrontAndBack|Front|Back)\b", RegexOptions.IgnoreCase);
            if (cullMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, cullMatch.Index);
                cull = (int)ParseCullMode(cullMatch.Groups[1].Value, line, column);
                hasState = true;
            }

            Match zWriteMatch = Regex.Match(stateSource, @"\bZWrite\s+(On|Off)\b", RegexOptions.IgnoreCase);
            if (zWriteMatch.Success)
            {
                zWrite = zWriteMatch.Groups[1].Value.Equals("On", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
                hasState = true;
            }

            Match zTestMatch = Regex.Match(stateSource, @"\bZTest\s+(Disabled|Never|Less|Equal|LEqual|Greater|NotEqual|GEqual|Always)\b", RegexOptions.IgnoreCase);
            if (zTestMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, zTestMatch.Index);
                zTest = (int)ParseCompareFunction(zTestMatch.Groups[1].Value, line, column);
                hasState = true;
            }

            Match colorMaskMatch = Regex.Match(stateSource, @"\bColorMask\s+([A-Za-z0-9]+)\b", RegexOptions.IgnoreCase);
            if (colorMaskMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, colorMaskMatch.Index);
                colorMask = new ShaderLabFloatProperty(
                    ParseColorMask(colorMaskMatch.Groups[1].Value, line, column),
                    "ColorMask");
                hasState = true;
            }

            Match alphaToMaskMatch = Regex.Match(stateSource, @"\bAlphaToMask\s+(On|Off)\b", RegexOptions.IgnoreCase);
            if (alphaToMaskMatch.Success)
            {
                alphaToMask = new ShaderLabFloatProperty(
                    alphaToMaskMatch.Groups[1].Value.Equals("On", StringComparison.OrdinalIgnoreCase) ? 1f : 0f,
                    "AlphaToMask");
                hasState = true;
            }

            Match offsetMatch = Regex.Match(stateSource, @"\bOffset\s+([-+]?\d*\.?\d+)\s*,?\s*([-+]?\d*\.?\d+)", RegexOptions.IgnoreCase);
            if (offsetMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, offsetMatch.Index);
                offsetFactor = new ShaderLabFloatProperty(ParseFloat(offsetMatch.Groups[1].Value, line, column), "OffsetFactor");
                offsetUnits = new ShaderLabFloatProperty(ParseFloat(offsetMatch.Groups[2].Value, line, column), "OffsetUnits");
                hasState = true;
            }

            Match blendOpMatch = Regex.Match(stateSource, @"\bBlendOp\s+([A-Za-z]+)(?:\s*,\s*([A-Za-z]+))?", RegexOptions.IgnoreCase);
            if (blendOpMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, blendOpMatch.Index);
                blendOp = new ShaderLabFloatProperty((int)ParseBlendOp(blendOpMatch.Groups[1].Value, line, column), "BlendOp");
                if (blendOpMatch.Groups[2].Success)
                {
                    blendOpAlpha = new ShaderLabFloatProperty((int)ParseBlendOp(blendOpMatch.Groups[2].Value, line, column), "BlendOpAlpha");
                }

                hasState = true;
            }

            Match blendOffMatch = Regex.Match(stateSource, @"\bBlend\s+Off\b", RegexOptions.IgnoreCase);
            Match blendMatch = Regex.Match(stateSource, @"\bBlend\s+([A-Za-z]+)\s+([A-Za-z]+)(?:\s*,\s*([A-Za-z]+)\s+([A-Za-z]+))?", RegexOptions.IgnoreCase);
            if (blendOffMatch.Success)
            {
                hasState = true;
            }
            else if (blendMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stateSource, blendMatch.Index);
                srcBlend = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[1].Value, line, column), "SrcBlend");
                dstBlend = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[2].Value, line, column), "DstBlend");
                if (blendMatch.Groups[3].Success && blendMatch.Groups[4].Success)
                {
                    srcBlendAlpha = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[3].Value, line, column), "SrcBlendAlpha");
                    dstBlendAlpha = new ShaderLabFloatProperty((int)ParseBlendMode(blendMatch.Groups[4].Value, line, column), "DstBlendAlpha");
                }
                else
                {
                    srcBlendAlpha = srcBlend;
                    dstBlendAlpha = dstBlend;
                }

                hasState = true;
            }

            ShaderLabFloatProperty? stencilRef = null;
            ShaderLabFloatProperty? stencilReadMask = null;
            ShaderLabFloatProperty? stencilWriteMask = null;
            ShaderLabStencilOp? stencilOp = null;
            ShaderLabStencilOp? stencilOpFront = null;
            ShaderLabStencilOp? stencilOpBack = null;
            if (!string.IsNullOrWhiteSpace(stencilSource))
            {
                ParseStencilState(
                    stencilSource,
                    out stencilRef,
                    out stencilReadMask,
                    out stencilWriteMask,
                    out stencilOp,
                    out stencilOpFront,
                    out stencilOpBack);
                hasState = true;
            }

            return hasState
                ? new ShaderLabRenderState(
                    cull,
                    zTest,
                    zWrite,
                    stencilOp,
                    stencilOpBack,
                    stencilOpFront,
                    colorMask,
                    alphaToMask,
                    offsetFactor,
                    offsetUnits,
                    blendOp,
                    blendOpAlpha,
                    srcBlend,
                    dstBlend,
                    srcBlendAlpha,
                    dstBlendAlpha,
                    stencilRef,
                    stencilReadMask,
                    stencilWriteMask)
                : null;
        }

        private static void ParseStencilState(
            string stencilSource,
            out ShaderLabFloatProperty? stencilRef,
            out ShaderLabFloatProperty? stencilReadMask,
            out ShaderLabFloatProperty? stencilWriteMask,
            out ShaderLabStencilOp? stencilOp,
            out ShaderLabStencilOp? stencilOpFront,
            out ShaderLabStencilOp? stencilOpBack)
        {
            stencilRef = null;
            stencilReadMask = null;
            stencilWriteMask = null;
            stencilOp = null;
            stencilOpFront = null;
            stencilOpBack = null;

            Match refMatch = Regex.Match(stencilSource, @"\bRef\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (refMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stencilSource, refMatch.Index);
                stencilRef = new ShaderLabFloatProperty(ParseFloat(refMatch.Groups[1].Value, line, column), "StencilRef");
            }

            Match readMaskMatch = Regex.Match(stencilSource, @"\bReadMask\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (readMaskMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stencilSource, readMaskMatch.Index);
                stencilReadMask = new ShaderLabFloatProperty(ParseFloat(readMaskMatch.Groups[1].Value, line, column), "StencilReadMask");
            }

            Match writeMaskMatch = Regex.Match(stencilSource, @"\bWriteMask\s+([0-9]+)\b", RegexOptions.IgnoreCase);
            if (writeMaskMatch.Success)
            {
                (int line, int column) = GetSourcePosition(stencilSource, writeMaskMatch.Index);
                stencilWriteMask = new ShaderLabFloatProperty(ParseFloat(writeMaskMatch.Groups[1].Value, line, column), "StencilWriteMask");
            }

            if (TryParseStencilOpValue(stencilSource, "Comp", out ShaderLabFloatProperty comp)
                | TryParseStencilOpValue(stencilSource, "Pass", out ShaderLabFloatProperty pass)
                | TryParseStencilOpValue(stencilSource, "Fail", out ShaderLabFloatProperty fail)
                | TryParseStencilOpValue(stencilSource, "ZFail", out ShaderLabFloatProperty zFail))
            {
                stencilOp = new ShaderLabStencilOp(comp, pass, fail, zFail);
            }

            if (TryParseStencilOpValue(stencilSource, "CompFront", out ShaderLabFloatProperty compFront)
                | TryParseStencilOpValue(stencilSource, "PassFront", out ShaderLabFloatProperty passFront)
                | TryParseStencilOpValue(stencilSource, "FailFront", out ShaderLabFloatProperty failFront)
                | TryParseStencilOpValue(stencilSource, "ZFailFront", out ShaderLabFloatProperty zFailFront))
            {
                stencilOpFront = new ShaderLabStencilOp(compFront, passFront, failFront, zFailFront);
            }

            if (TryParseStencilOpValue(stencilSource, "CompBack", out ShaderLabFloatProperty compBack)
                | TryParseStencilOpValue(stencilSource, "PassBack", out ShaderLabFloatProperty passBack)
                | TryParseStencilOpValue(stencilSource, "FailBack", out ShaderLabFloatProperty failBack)
                | TryParseStencilOpValue(stencilSource, "ZFailBack", out ShaderLabFloatProperty zFailBack))
            {
                stencilOpBack = new ShaderLabStencilOp(compBack, passBack, failBack, zFailBack);
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

            (int line, int column) = GetSourcePosition(source, match.Index);
            if (key.StartsWith("Comp", StringComparison.OrdinalIgnoreCase))
            {
                result = new ShaderLabFloatProperty((int)ParseCompareFunction(match.Groups[1].Value, line, column), key);
                return true;
            }

            result = new ShaderLabFloatProperty((int)ParseStencilOp(match.Groups[1].Value, line, column), key);
            return true;
        }
    }
}
