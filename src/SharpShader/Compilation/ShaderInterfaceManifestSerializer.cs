using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using SharpShader.Compilation.Internal;

namespace SharpShader.Compilation
{
    public static class ShaderInterfaceManifestSerializer
    {
        public static string Serialize(ShaderInterfaceManifest manifest)
        {
            return Encoding.UTF8.GetString(SerializeToUtf8Bytes(manifest));
        }

        public static byte[] SerializeToUtf8Bytes(ShaderInterfaceManifest manifest)
        {
            ArgumentNullException.ThrowIfNull(manifest);
            ShaderInterfaceManifestDocument document = ShaderInterfaceManifestCodec.Encode(manifest);
            return JsonSerializer.SerializeToUtf8Bytes(
                document,
                ShaderInterfaceManifestJsonContext.Default.ShaderInterfaceManifestDocument);
        }

        public static ShaderInterfaceManifest Deserialize(string json)
        {
            ArgumentNullException.ThrowIfNull(json);
            return Deserialize(Encoding.UTF8.GetBytes(json));
        }

        public static ShaderInterfaceManifest Deserialize(ReadOnlySpan<byte> utf8Json)
        {
            ValidateJsonObject(utf8Json);

            ShaderInterfaceManifestDocument? document = JsonSerializer.Deserialize(
                utf8Json,
                ShaderInterfaceManifestJsonContext.Default.ShaderInterfaceManifestDocument);
            if (document is null)
            {
                throw new JsonException("Shader interface manifest JSON must contain an object.");
            }

            try
            {
                return ShaderInterfaceManifestCodec.Decode(document);
            }
            catch (JsonException)
            {
                throw;
            }
            catch (Exception exception) when (
                exception is ArgumentException
                or FormatException
                or InvalidOperationException
                or OverflowException)
            {
                throw new JsonException(
                    "Shader interface manifest JSON violates the schema or binding ABI contract.",
                    exception);
            }
        }

        private static void ValidateJsonObject(ReadOnlySpan<byte> utf8Json)
        {
            Utf8JsonReader reader = new(
                utf8Json,
                new JsonReaderOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 128,
                });
            using JsonDocument document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Shader interface manifest JSON must contain an object.");
            }

            ValidateNoDuplicateProperties(document.RootElement, "$");
            if (reader.Read())
            {
                throw new JsonException("Shader interface manifest JSON contains trailing data.");
            }
        }

        private static void ValidateNoDuplicateProperties(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    HashSet<string> propertyNames = new(StringComparer.Ordinal);
                    foreach (JsonProperty property in element.EnumerateObject())
                    {
                        if (!propertyNames.Add(property.Name))
                        {
                            throw new JsonException(
                                $"Shader interface manifest JSON contains duplicate property {path}.{property.Name}.");
                        }

                        ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}");
                    }

                    break;
                }
                case JsonValueKind.Array:
                {
                    int index = 0;
                    foreach (JsonElement item in element.EnumerateArray())
                    {
                        ValidateNoDuplicateProperties(item, $"{path}[{index}]");
                        ++index;
                    }

                    break;
                }
            }
        }
    }
}
