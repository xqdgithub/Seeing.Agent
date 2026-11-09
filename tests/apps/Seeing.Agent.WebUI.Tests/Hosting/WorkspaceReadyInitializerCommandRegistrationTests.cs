using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using FluentAssertions;
using Seeing.Agent.WebUI.Hosting;
using Xunit;

namespace Seeing.Agent.WebUI.Tests.Hosting;

/// <summary>
/// 守卫「宿主补初始化到底调用了哪一个 InitializeCommands」。
/// <para>
/// 背景（2026-11-09）：<c>InitializeCommands(this IServiceProvider)</c> 曾有两份同扩展方法——
/// </para>
/// <list type="bullet">
/// <item><c>Seeing.Agent.Core.Extensions.ServiceCollectionExtensions.InitializeCommands</c>：
/// 只处理 <c>additionalCommands</c> 与 <c>ICommandDiscoveryInitializer</c>，二者在本仓库均无人使用，
/// 实际是一个**空转**实现。</item>
/// <item><c>Seeing.Agent.Hosting.HostingServiceCollectionExtensions.InitializeCommands</c>：
/// 才是真正把 <c>BuiltInCommands</c>（含 <c>/compact</c>）注册进 <c>ICommandRegistry</c> 的实现。</item>
/// </list>
/// <para>
/// 二者同名且都可无参调用，绑定结果只取决于调用方导入了哪个命名空间。
/// 一旦 <see cref="WorkspaceReadyInitializer"/> 少了 <c>using Seeing.Agent.Hosting;</c>，
/// 编译器就会静默绑到 Core 的空转版本：编译通过、启动成功，但斜杠命令列表为空
/// （<c>/compact</c> 等命令「无法使用」且没有任何报错）。
/// </para>
/// <para>
/// Core 侧那个空转重载与配套的 <c>CommandSystemOptions</c> / <c>ICommandDiscoveryInitializer</c> / <c>AddCommandSystem</c>
/// 已在本日期一并删除，同形误绑在编译期就不可能再发生。
/// 本测试仍保留：它钉住的是「补初始化必须真正注册命令」这一行为，
/// 未来若有人再为命令初始化引入第二个同名入口，这里会先失败。
/// </para>
/// <para>
/// 因此这里直接检查编译产物里的调用目标，而不是靠运行时行为——
/// 运行时差异会被「命令本来就没注册」这个前提取代，测不出绑定错误。
/// </para>
/// </summary>
public class WorkspaceReadyInitializerCommandRegistrationTests
{
    [Fact]
    public void RunIfNeeded_Should_Bind_To_Hosting_InitializeCommands_Not_Core()
    {
        var targets = ReadInitializeCommandsCallTargets(typeof(WorkspaceReadyInitializer).Assembly.Location);

        targets.Should().Contain("Seeing.Agent.Hosting.HostingServiceCollectionExtensions",
            "补初始化必须调用 Hosting 的重载：只有它会把 BuiltInCommands（含 /compact）注册进 ICommandRegistry");

        targets.Should().NotContain("Seeing.Agent.Core.Extensions.ServiceCollectionExtensions",
            "Core 侧那个同名重载（只处理 additionalCommands / ICommandDiscoveryInitializer）"
            + "已于 2026-11-09 连同 CommandSystemOptions 一并删除；此处兜底防止它被重新引入——"
            + "它调起来什么都不做，编译与启动都不会报错，但所有斜杠命令会静默消失");
    }

    /// <summary>
    /// 扫描 <c>WorkspaceReadyInitializer</c> 类型中所有方法的 IL，
    /// 收集对名为 <c>InitializeCommands</c> 的成员引用所归属的类型全名。
    /// </summary>
    private static List<string> ReadInitializeCommandsCallTargets(string assemblyPath)
    {
        using var fs = File.OpenRead(assemblyPath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var targets = new List<string>();

        foreach (var typeHandle in md.TypeDefinitions)
        {
            var typeDef = md.GetTypeDefinition(typeHandle);
            if (md.GetString(typeDef.Name) != nameof(WorkspaceReadyInitializer))
                continue;

            foreach (var methodHandle in typeDef.GetMethods())
            {
                var methodDef = md.GetMethodDefinition(methodHandle);
                if (methodDef.RelativeVirtualAddress == 0)
                    continue;

                var il = pe.GetMethodBody(methodDef.RelativeVirtualAddress).GetILBytes();
                if (il is null)
                    continue;

                // call / callvirt 后紧跟 4 字节 metadata token；MemberRef 表的高位是 0x0A。
                for (var i = 0; i + 4 < il.Length; i++)
                {
                    if (il[i] != 0x28 && il[i] != 0x6F)
                        continue;

                    var token = BitConverter.ToInt32(il, i + 1);
                    if ((token >> 24) != 0x0A)
                        continue;

                    var memberRef = md.GetMemberReference(
                        MetadataTokens.MemberReferenceHandle(token & 0x00FFFFFF));
                    if (md.GetString(memberRef.Name) != "InitializeCommands")
                        continue;

                    if (memberRef.Parent.Kind != HandleKind.TypeReference)
                        continue;

                    var typeRef = md.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    targets.Add($"{md.GetString(typeRef.Namespace)}.{md.GetString(typeRef.Name)}");
                }
            }
        }

        return targets;
    }
}
