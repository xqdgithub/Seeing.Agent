namespace Seeing.Agent.WebUI.Models
{
    /// <summary>
    /// 快速命令工具视图模型（用于浮窗展示）
    /// </summary>
    public class QuickToolViewModel
    {
        /// <summary>工具名称</summary>
        public string Name { get; set; } = "";
        
        /// <summary>工具描述</summary>
        public string Description { get; set; } = "";
        
        /// <summary>参数提示（简要描述参数）</summary>
        public string? ParametersHint { get; set; }
        
        /// <summary>工具 ID（用于调用）</summary>
        public string Id { get; set; } = "";
    }
    
    /// <summary>
    /// 快速命令技能视图模型（用于浮窗展示）
    /// </summary>
    public class QuickSkillViewModel
    {
        /// <summary>技能名称</summary>
        public string Name { get; set; } = "";
        
        /// <summary>技能描述</summary>
        public string Description { get; set; } = "";
        
        /// <summary>版本</summary>
        public string? Version { get; set; }
        
        /// <summary>作者</summary>
        public string? Author { get; set; }
    }
}