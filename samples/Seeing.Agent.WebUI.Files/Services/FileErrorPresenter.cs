using Seeing.Agent.Abstractions.Files;

namespace Seeing.Agent.WebUI.Files.Services;

/// <summary>
/// 文件读取/打开异常 → 用户可见消息（设计规格「错误契约 UI 映射」）。
/// 依赖契约层异常类型（<see cref="FileTooLargeException"/> / <see cref="FileNotEditableException"/>
/// 定义于 <c>Seeing.Agent.Abstractions.Files</c>），可强类型匹配，编译期受保护。
/// </summary>
public static class FileErrorPresenter
{
    /// <summary>把异常映射为界面提示文案（不含堆栈；未知异常回退通用文案）。</summary>
    public static string Describe(Exception ex) => ex switch
    {
        UnauthorizedAccessException => "路径不在工作区",
        FileNotFoundException => "文件不存在",
        DirectoryNotFoundException => "文件不存在",
        FileTooLargeException => "文件过大，仅预览",
        FileNotEditableException => "该文件编码不支持编辑",
        IOException => "读取失败，请重试",
        OperationCanceledException => "操作已取消",
        _ => "无法打开该文件",
    };
}
