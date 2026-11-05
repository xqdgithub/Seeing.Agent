namespace Seeing.Agent.Abstractions.Llm;

/// <summary>
/// 「协议 → 客户端工厂」的注册记录：协议在这里是<b>服务键</b>，一个协议只能绑一个工厂。
/// <para><b>为什么不是 keyed DI</b>：MS.DI 没有公开 API 枚举 keyed 注册，而 WebUI
/// 「添加 Provider」的协议下拉需要枚举「当前可用的协议」。用 keyed 就得另维护一份 key 列表，
/// 于是协议又有了两个真相源。一条不可变记录既保证唯一绑定，又能被注册表整体枚举。</para>
/// <para>同一工厂可登记多个协议（如 OpenAI 工厂同时服务 <c>openai</c> 与 <c>openai-responses</c>）：
/// 登记多条记录、共享同一工厂实例即可。</para>
/// </summary>
/// <param name="Protocol">协议键（已规范化，见 <see cref="ProviderTypes.Normalize"/>）。</param>
/// <param name="Factory">该协议的客户端工厂。</param>
public sealed record LlmClientFactoryRegistration(string Protocol, ILlmClientFactory Factory);
