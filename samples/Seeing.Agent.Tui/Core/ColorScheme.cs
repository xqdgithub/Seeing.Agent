namespace Seeing.Agent.Tui.Core;

/// <summary>
/// 颜色方案配置 - 用于 Spectre.Console 渲染
/// </summary>
public static class ColorScheme
{
    /// <summary>用户消息颜色</summary>
    public static string UserColor => "cyan";
    
    /// <summary>助手消息颜色</summary>
    public static string AssistantColor => "green";
    
    /// <summary>工具调用颜色</summary>
    public static string ToolColor => "blue";
    
    /// <summary>思考过程颜色</summary>
    public static string ReasoningColor => "grey";
    
    /// <summary>错误颜色</summary>
    public static string ErrorColor => "red";
    
    /// <summary>警告颜色</summary>
    public static string WarningColor => "yellow";
    
    /// <summary>成功颜色</summary>
    public static string SuccessColor => "green";
    
    /// <summary>待处理颜色</summary>
    public static string PendingColor => "yellow";
    
    /// <summary>运行中颜色</summary>
    public static string RunningColor => "blue";
    
    /// <summary>系统消息颜色</summary>
    public static string SystemColor => "white";
    
    /// <summary>标题颜色</summary>
    public static string HeaderColor => "bold yellow";
    
    /// <summary>边框颜色</summary>
    public static string BorderColor => "white";
    
    /// <summary>错误边框颜色</summary>
    public static string ErrorBorderColor => "red";
    
    /// <summary>工具边框颜色</summary>
    public static string ToolBorderColor => "blue";
    
    /// <summary>折叠提示颜色</summary>
    public static string FoldHintColor => "dim";
    
    /// <summary>图标定义</summary>
    public static class Icons
    {
        /// <summary>用户图标</summary>
        public static string User => "[cyan]👤[/]";
        
        /// <summary>助手图标</summary>
        public static string Assistant => "[green]🤖[/]";
        
        /// <summary>工具图标</summary>
        public static string Tool => "[blue]🔧[/]";
        
        /// <summary>思考图标</summary>
        public static string Reasoning => "[grey]💭[/]";
        
        /// <summary>错误图标</summary>
        public static string Error => "[red]❌[/]";
        
        /// <summary>成功图标</summary>
        public static string Success => "[green]✓[/]";
        
        /// <summary>待处理图标</summary>
        public static string Pending => "[yellow]⏳[/]";
        
        /// <summary>运行中图标</summary>
        public static string Running => "[blue]🔄[/]";
        
        /// <summary>失败图标</summary>
        public static string Failed => "[red]✗[/]";
        
        /// <summary>拒绝图标</summary>
        public static string Rejected => "[grey]⊘[/]";
        
        /// <summary>折叠图标</summary>
        public static string Folded => "[dim]▶[/]";
        
        /// <summary>展开图标</summary>
        public static string Expanded => "[dim]▼[/]";
    }
}