using System;
using System.Collections.Generic;

namespace Seeing.Agent.WebUI.Services
{
    /// <summary>
    /// 错误处理服务，用于统一管理错误消息和状态通知
    /// </summary>
    public class ErrorHandlingService
    {
        /// <summary>
        /// 错误消息列表
        /// </summary>
        public List<ErrorEntry> Errors { get; set; } = new();

        /// <summary>
        /// 成功消息列表
        /// </summary>
        public List<string> SuccessMessages { get; set; } = new();

        /// <summary>
        /// 信息消息列表
        /// </summary>
        public List<string> InfoMessages { get; set; } = new();

        /// <summary>
        /// 当状态变更时触发
        /// </summary>
        public event Action? OnStateChanged;

        /// <summary>
        /// 添加错误消息
        /// </summary>
        public void AddError(string message, string? code = null, Exception? exception = null)
        {
            Errors.Add(new ErrorEntry
            {
                Message = message,
                Code = code,
                Exception = exception,
                Timestamp = DateTime.Now
            });
            
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 添加成功消息
        /// </summary>
        public void AddSuccess(string message)
        {
            SuccessMessages.Add(message);
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 添加信息消息
        /// </summary>
        public void AddInfo(string message)
        {
            InfoMessages.Add(message);
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 清除所有错误
        /// </summary>
        public void ClearErrors()
        {
            Errors.Clear();
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 清除所有成功消息
        /// </summary>
        public void ClearSuccess()
        {
            SuccessMessages.Clear();
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 清除所有信息消息
        /// </summary>
        public void ClearInfo()
        {
            InfoMessages.Clear();
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 清除所有消息
        /// </summary>
        public void ClearAll()
        {
            Errors.Clear();
            SuccessMessages.Clear();
            InfoMessages.Clear();
            OnStateChanged?.Invoke();
        }

        /// <summary>
        /// 处理异常并转换为用户友好的消息
        /// </summary>
        public string HandleException(Exception ex)
        {
            var message = GetUserFriendlyMessage(ex);
            AddError(message, null, ex);
            return message;
        }

        /// <summary>
        /// 将异常转换为用户友好的消息
        /// </summary>
        private string GetUserFriendlyMessage(Exception ex)
        {
            return ex switch
            {
                OperationCanceledException => "操作已被取消",
                TimeoutException => "请求超时，请稍后重试",
                UnauthorizedAccessException => "权限不足，无法执行此操作",
                ArgumentException => "输入参数无效，请检查后重试",
                InvalidOperationException => "操作无效，当前状态不允许执行",
                _ => $"发生错误: {ex.Message}"
            };
        }

        /// <summary>
        /// 错误条目
        /// </summary>
        public class ErrorEntry
        {
            public string Message { get; set; } = "";
            public string? Code { get; set; }
            public Exception? Exception { get; set; }
            public DateTime Timestamp { get; set; }
        }
    }
}