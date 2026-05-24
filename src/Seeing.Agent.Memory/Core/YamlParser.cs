// YamlDotNet is referenced in Directory.Packages.props (v16.0.0).
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Seeing.Agent.Memory.Core
{
    /// <summary>
    /// Simple YAML Front Matter parser for Markdown documents.
    /// Supports:
    /// - ParseYamlFrontMatter: parse key/value pairs from the YAML front matter block
    /// - ExtractMarkdownBody: return the Markdown body without the YAML front matter
    /// </summary>
    public static class YamlParser
    {
        /// <summary>
        /// Parses YAML front matter from the given content. Front matter is expected to be between lines containing only '---'.
        /// Returns a dictionary of key-value pairs. If no front matter is found, an empty dictionary is returned.
        /// </summary>
        /// <param name="content">Markdown content containing optional YAML front matter.</param>
        /// <returns>Dictionary of front matter values with string keys and object? values.</returns>
        public static Dictionary<string, object?> ParseYamlFrontMatter(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return new Dictionary<string, object?>();

            var yaml = ExtractYamlSection(content);
            if (string.IsNullOrWhiteSpace(yaml)) return new Dictionary<string, object?>();

            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var result = deserializer.Deserialize<Dictionary<string, object?>>(yaml);
            return result ?? new Dictionary<string, object?>();
        }

        /// <summary>
        /// Extracts the Markdown body by removing the YAML front matter block (delimited by ---).
        /// If no front matter is found, returns the original content.
        /// </summary>
        /// <param name="content">Markdown content.</param>
        /// <returns>Markdown body without YAML front matter.</returns>
        public static string ExtractMarkdownBody(string content)
        {
            if (string.IsNullOrEmpty(content)) return string.Empty;

            // Read lines to locate front matter markers reliably
            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int start = -1;
            int end = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    if (start == -1)
                    {
                        start = i; // opening delimiter
                    }
                    else
                    {
                        end = i; // closing delimiter
                        break;
                    }
                }
            }

            if (start != -1 && end != -1 && end > start)
            {
                // Body starts after the closing delimiter line
                int bodyStartIndex = end + 1;
                if (bodyStartIndex >= lines.Length) return string.Empty;
                return string.Join("\n", lines, bodyStartIndex, lines.Length - bodyStartIndex);
            }

            // No front matter found
            return content;
        }

        // Helper to extract the YAML block between the first pair of '---' markers
        private static string ExtractYamlSection(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return string.Empty;

            var lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            int start = -1;
            int end = -1;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].Trim() == "---")
                {
                    if (start == -1)
                    {
                        start = i; // opening delimiter
                    }
                    else
                    {
                        end = i; // closing delimiter
                        break;
                    }
                }
            }

            if (start != -1 && end != -1 && end > start + 0)
            {
                var yamlLines = new List<string>();
                for (int i = start + 1; i < end; i++) yamlLines.Add(lines[i]);
                return string.Join("\n", yamlLines);
            }

            return string.Empty;
        }
    }
}
