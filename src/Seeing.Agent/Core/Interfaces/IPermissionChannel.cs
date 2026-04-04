namespace Seeing.Agent.Core.Interfaces
{
    /// <summary>
    /// 权限请求通道接口 - 处理需要用户确认的权限请求
    /// </summary>
    public interface IPermissionChannel
    {
        /// <summary>请求权限确认</summary>
        /// <param name="request">权限请求</param>
        /// <returns>用户是否批准</returns>
        Task<bool> RequestConfirmationAsync(PermissionRequest request);
        
        /// <summary>设置确认回调处理器（由应用层注入）</summary>
        void SetConfirmationHandler(Func<PermissionRequest, Task<bool>> handler);
    }
}