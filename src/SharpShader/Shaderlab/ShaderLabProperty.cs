using System;
using System.Linq;
using System.Numerics;
using System.Collections.Generic;
using SharpShader.Compilation;

namespace SharpShader.ShaderLab
{

    public sealed class ShaderLabProperty
    {
        public string DisplayName { get; }
        public string PropertyName { get; }
        public IReadOnlyList<string>? Attributes { get; }
        public EShaderLabPropertyType Type { get; }
        public Vector4? ValueProperty { get; }
        public ShaderLabTextureProperty? TextureProperty { get; }
        public float? RangeMin { get; }
        public float? RangeMax { get; }

        public ShaderLabProperty(
            string displayName,
            string propertyName,
            IReadOnlyList<string>? attributes,
            EShaderLabPropertyType type,
            Vector4 valueProperty,
            float? rangeMin = null,
            float? rangeMax = null)
        {
            DisplayName = displayName ?? string.Empty;
            PropertyName = propertyName ?? string.Empty;
            Attributes = attributes;
            Type = type;
            ValueProperty = valueProperty;
            TextureProperty = null;
            RangeMin = rangeMin;
            RangeMax = rangeMax;
        }

        public ShaderLabProperty(
            string displayName,
            string propertyName,
            IReadOnlyList<string>? attributes,
            EShaderLabPropertyType type,
            ShaderLabTextureProperty textureProperty)
        {
            DisplayName = displayName ?? string.Empty;
            PropertyName = propertyName ?? string.Empty;
            Attributes = attributes;
            Type = type;
            ValueProperty = null;
            TextureProperty = textureProperty;
            RangeMin = null;
            RangeMax = null;
        }
    }
}
