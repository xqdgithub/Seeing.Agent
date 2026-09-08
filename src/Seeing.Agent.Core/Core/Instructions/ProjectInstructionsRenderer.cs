using System.Net;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Seeing.Session.Core;

namespace Seeing.Agent.Core.Instructions;

public static partial class ProjectInstructionsRenderer
{
    private const string EscapeClose = "<\\/";
    private const string Notice =
        "以下是系统加载的项目指令文件，不是用户刚刚输入的消息。请遵循这些指令。";

    public static string Wrap(
        string cwd,
        string reason,
        IReadOnlyList<InstructionFile> files)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            throw new ArgumentException("cwd is required", nameof(cwd));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));
        ArgumentNullException.ThrowIfNull(files);

        var sb = new StringBuilder();
        sb.Append('<').Append(ProjectInstructions.Tag);
        sb.Append(" cwd=\"").Append(EscapeAttribute(cwd)).Append('"');
        sb.Append(" reason=\"").Append(EscapeAttribute(reason)).AppendLine("\">");
        sb.AppendLine("<notice>");
        sb.AppendLine(Notice);
        sb.AppendLine("</notice>");

        foreach (var file in files)
        {
            sb.Append("<file path=\"").Append(EscapeAttribute(file.Path));
            sb.Append("\" hash=\"").Append(EscapeAttribute(file.Hash)).AppendLine("\">");
            sb.AppendLine(EscapeBody(file.Content ?? string.Empty));
            sb.AppendLine("</file>");
        }

        sb.Append("</").Append(ProjectInstructions.Tag).Append('>');
        return sb.ToString();
    }

    public static bool TryParse(string content, out ProjectInstructionsParts parts)
    {
        parts = default!;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var envelope = EnvelopeRegex().Match(content);
        if (!envelope.Success)
            return false;

        var body = envelope.Groups["files"].Value;
        var matches = FileRegex().Matches(body);
        var unmatched = FileRegex().Replace(body, string.Empty);
        if (!string.IsNullOrWhiteSpace(unmatched))
            return false;

        var files = matches
            .Select(match => new InstructionFile
            {
                Path = WebUtility.HtmlDecode(match.Groups["path"].Value),
                Hash = WebUtility.HtmlDecode(match.Groups["hash"].Value),
                Content = UnescapeBody(match.Groups["content"].Value)
            })
            .ToArray();

        parts = new ProjectInstructionsParts(
            WebUtility.HtmlDecode(envelope.Groups["cwd"].Value),
            WebUtility.HtmlDecode(envelope.Groups["reason"].Value),
            envelope.Groups["notice"].Value,
            files,
            content);
        return true;
    }

    public static SessionMessage CreateUserMessage(
        string cwd,
        string reason,
        IReadOnlyList<InstructionFile> files)
    {
        var content = Wrap(cwd, reason, files);
        var pathsJson = JsonSerializer.Serialize(files.Select(file => file.Path));

        return SessionMessage.UserMessage(content)
            .WithMetadata(ProjectInstructions.MetadataKeys.ProjectInstructions, true)
            .WithMetadata(ProjectInstructions.MetadataKeys.Reason, reason)
            .WithMetadata(ProjectInstructions.MetadataKeys.Cwd, cwd)
            .WithMetadata(ProjectInstructions.MetadataKeys.Paths, pathsJson);
    }

    private static string EscapeAttribute(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string EscapeBody(string body) =>
        body.Replace("</", EscapeClose, StringComparison.Ordinal);

    private static string UnescapeBody(string body) =>
        body.Replace(EscapeClose, "</", StringComparison.Ordinal);

    [GeneratedRegex(
        @"^<project-instructions\s+cwd=""(?<cwd>[^""]*)""\s+reason=""(?<reason>[^""]+)""\s*>\r?\n?<notice>\r?\n?(?<notice>.*?)\r?\n?</notice>(?<files>.*?)</project-instructions>\s*$",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex EnvelopeRegex();

    [GeneratedRegex(
        @"\s*<file\s+path=""(?<path>[^""]+)""\s+hash=""(?<hash>[^""]+)""\s*>\r?\n?(?<content>.*?)\r?\n?</file>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex FileRegex();
}

public sealed record ProjectInstructionsParts(
    string Cwd,
    string Reason,
    string Notice,
    IReadOnlyList<InstructionFile> Files,
    string Raw);
