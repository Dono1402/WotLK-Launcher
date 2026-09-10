using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace WotLK.Launcher;

internal static class BoundedJsonHttpContent
{
    private const int ReadBufferSize = 64 * 1024;

    internal static async Task<byte[]> ReadAsync(
        HttpContent content,
        int maximumBytes,
        string resourceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));

        long? declaredLength = content.Headers.ContentLength;
        if (declaredLength is < 0 || declaredLength > maximumBytes)
        {
            throw new InvalidDataException($"{resourceName} dépasse la taille maximale autorisée.");
        }

        int initialCapacity = declaredLength is > 0
            ? checked((int)declaredLength.Value)
            : Math.Min(ReadBufferSize, maximumBytes);
        using MemoryStream buffered = new(initialCapacity);
        await using Stream source = await content.ReadAsStreamAsync(cancellationToken);
        byte[] buffer = new byte[ReadBufferSize];
        long received = 0;

        while (true)
        {
            // Leave room for one probe byte once the limit is reached. That byte
            // is rejected before it can be copied into the bounded buffer.
            int requested = (int)Math.Min(buffer.Length, maximumBytes - received + 1L);
            int read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);
            if (read == 0) break;
            if (received > maximumBytes - read)
            {
                throw new InvalidDataException($"{resourceName} dépasse la taille maximale autorisée.");
            }

            await buffered.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
        }

        return buffered.ToArray();
    }

    internal static void RejectDuplicateProperties(JsonElement root, string resourceName)
    {
        Inspect(root);
        return;

        void Inspect(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                // Callers deserialize property names case-insensitively, so names
                // that differ only by casing are duplicates for the resulting DTO.
                HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
                foreach (JsonProperty property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException(
                            $"{resourceName} contient une propriété JSON dupliquée: {property.Name}.");
                    }

                    Inspect(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray()) Inspect(item);
            }
        }
    }
}
