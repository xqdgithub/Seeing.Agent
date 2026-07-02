using System.Text.Json.Serialization;

namespace Seeing.Gateway.Models;

/// <summary>
/// 网关输入内容段（多模态 discriminated union）
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(GatewayTextContentPart), "text")]
[JsonDerivedType(typeof(GatewayImageContentPart), "image")]
[JsonDerivedType(typeof(GatewayFileContentPart), "file")]
[JsonDerivedType(typeof(GatewayAudioContentPart), "audio")]
public abstract record GatewayContentPart;

public record GatewayTextContentPart(string Text) : GatewayContentPart;

public record GatewayImageContentPart(string Url, string? MimeType = null) : GatewayContentPart;

public record GatewayFileContentPart(string Url, string? MimeType = null, string? Name = null) : GatewayContentPart;

public record GatewayAudioContentPart(string Url, string? MimeType = null) : GatewayContentPart;
