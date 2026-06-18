using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace electrostat.IO
{
    /// <summary>
    /// On-disk wrapper for a saved <c>.estat</c> case. The <see cref="Version"/> field lets
    /// the format evolve while older files remain loadable.
    /// </summary>
    /// <param name="Version">Schema version of the file (see <see cref="TransformerSerializer.CurrentVersion"/>).</param>
    /// <param name="Transformer">The serialized transformer / input data set.</param>
    public sealed record CaseFile(int Version, Transformer Transformer);

    /// <summary>
    /// Reads and writes <see cref="Transformer"/> input data as <c>.estat</c> JSON documents.
    /// Only the input data is persisted; computed results are intentionally excluded.
    /// </summary>
    public static class TransformerSerializer
    {
        /// <summary>Current schema version written to new files.</summary>
        public const int CurrentVersion = 1;

        /// <summary>The file extension (including the dot) used for saved cases.</summary>
        public const string FileExtension = ".estat";

        /// <summary>
        /// Shared options: indented for readability, case-insensitive on read, enums written
        /// as strings (so <c>GeometryType</c> is human-readable). The default property and
        /// dictionary-key naming policies are kept so <c>Voltages</c> keys (electrode names
        /// such as <c>"SR1_Metal"</c>) are preserved verbatim.
        /// </summary>
        private static readonly JsonSerializerOptions Options = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        /// <summary>Serialize a transformer to an indented <c>.estat</c> JSON string.</summary>
        public static string Serialize(Transformer transformer)
        {
            ArgumentNullException.ThrowIfNull(transformer);
            return JsonSerializer.Serialize(new CaseFile(CurrentVersion, transformer), Options);
        }

        /// <summary>
        /// Deserialize a transformer from an <c>.estat</c> JSON string. A missing or unknown
        /// <c>Version</c> is tolerated; only the embedded transformer is required.
        /// </summary>
        /// <exception cref="InvalidDataException">The JSON is empty or contains no transformer.</exception>
        public static Transformer Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new InvalidDataException("The file is empty or not a valid .estat document.");

            CaseFile? file;
            try
            {
                file = JsonSerializer.Deserialize<CaseFile>(json, Options);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("The file is not a valid .estat document.", ex);
            }

            if (file?.Transformer is null)
                throw new InvalidDataException("The file does not contain a transformer definition.");

            return file.Transformer;
        }

        /// <summary>Serialize <paramref name="transformer"/> and write it to <paramref name="path"/>.</summary>
        public static Task SaveAsync(Transformer transformer, string path)
        {
            ArgumentNullException.ThrowIfNull(transformer);
            ArgumentException.ThrowIfNullOrEmpty(path);
            return File.WriteAllTextAsync(path, Serialize(transformer));
        }

        /// <summary>Read <paramref name="path"/> and deserialize the transformer it contains.</summary>
        public static async Task<Transformer> LoadAsync(string path)
        {
            ArgumentException.ThrowIfNullOrEmpty(path);
            string json = await File.ReadAllTextAsync(path).ConfigureAwait(false);
            return Deserialize(json);
        }
    }
}
