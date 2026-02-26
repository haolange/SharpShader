using System;
using System.Text.Json.Serialization;

namespace SharpShader.HLSLCrossCompiler;

public readonly struct ShaderModelVersion : IEquatable<ShaderModelVersion>
{
    [JsonConstructor]
    public ShaderModelVersion(int major, int minor)
    {
        Major = major;
        Minor = minor;

        if (!IsInRange)
        {
            throw new ArgumentOutOfRangeException(nameof(minor), "Shader model must be in range 6.0 to 6.8.");
        }
    }

    public int Major { get; }

    public int Minor { get; }

    public bool IsInRange => Major == 6 && Minor >= 0 && Minor <= 8;

    public override string ToString()
    {
        return $"{Major}.{Minor}";
    }

    public bool Equals(ShaderModelVersion other)
    {
        return Major == other.Major && Minor == other.Minor;
    }

    public override bool Equals(object? obj)
    {
        return obj is ShaderModelVersion other && Equals(other);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Major, Minor);
    }

    public static bool operator ==(ShaderModelVersion left, ShaderModelVersion right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ShaderModelVersion left, ShaderModelVersion right)
    {
        return !left.Equals(right);
    }
}
