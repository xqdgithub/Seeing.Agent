namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 权限动作
    /// </summary>
    public enum PermissionAction
    {
        /// <summary>允许</summary>
        Allow,
        
        /// <summary>拒绝</summary>
        Deny,
        
        /// <summary>询问用户</summary>
        Ask
    }

    /// <summary>
    /// 权限规则定义
    /// </summary>
    public class PermissionRule
    {
        /// <summary>权限名称</summary>
        public string Permission { get; set; } = string.Empty;
        
        /// <summary>匹配模式 (支持通配符)</summary>
        public string Pattern { get; set; } = "*";
        
        /// <summary>动作</summary>
        public PermissionAction Action { get; set; }
    }

    /// <summary>
    /// 规则引擎接口
    /// </summary>
    public interface IRuleEngine
    {
        /// <summary>添加规则</summary>
        void AddRule(PermissionRule rule);
        
        /// <summary>从配置加载规则</summary>
        void LoadFromConfig(Dictionary<string, object> config);
        
        /// <summary>合并规则集</summary>
        void MergeRules(IEnumerable<PermissionRule> rules);
        
        /// <summary>求值权限请求</summary>
        PermissionAction Evaluate(string permission, string pattern);
        
        /// <summary>检查工具是否被禁用</summary>
        bool IsToolDisabled(string toolName);
        
        /// <summary>获取所有规则</summary>
        IReadOnlyList<PermissionRule> GetRules();
    }
}