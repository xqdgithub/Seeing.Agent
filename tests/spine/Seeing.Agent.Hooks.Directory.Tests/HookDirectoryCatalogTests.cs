using FluentAssertions;
using Seeing.Agent.Abstractions.Hooks;
using Seeing.Agent.Hooks.Directory.Catalog;
using Seeing.Agent.Paths;
using Xunit;

namespace Seeing.Agent.Hooks.Directory.Tests;

public class HookDirectoryCatalogTests : IDisposable
{
    private readonly string _root;

    public HookDirectoryCatalogTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "seeing-hooks-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(_root);
    }

    public void Dispose() => System.IO.Directory.Delete(_root, recursive: true);

    [Fact]
    public void Classify_RootScript_UsesFileNameAsPoint()
    {
        var full = Place("tool.execute.before.js");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, firstLine: null, contentHash: "unused");

        file.Disposition.Should().Be(HookFileDisposition.Script);
        file.Point.Should().Be("tool.execute.before");
        file.RelativePath.Should().Be("tool.execute.before.js");
        file.ExplicitPriority.Should().BeNull();
        file.ContentHash.Should().BeNull();
        file.FullPath.Should().Be(full);
    }

    [Fact]
    public void Classify_NestedScript_UsesParentDirectoryAsPoint()
    {
        var full = Place("tool.execute.before/audit.js");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, firstLine: null, contentHash: null);

        file.Disposition.Should().Be(HookFileDisposition.Script);
        file.Point.Should().Be("tool.execute.before");
        file.RelativePath.Should().Be("tool.execute.before/audit.js");
    }

    [Theory]
    [InlineData("point/lib/a.js")]
    [InlineData("node_modules/a.js")]
    [InlineData(".cache/a.dll")]
    [InlineData("a.cjs")]
    [InlineData("a.mjs")]
    [InlineData("sub/a.dll")]
    public void Classify_NonHookLayout_IsIgnored(string relative)
    {
        var full = Place(relative);

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, firstLine: "// @priority 7", contentHash: "hash");

        file.Disposition.Should().Be(HookFileDisposition.Ignored);
        file.RelativePath.Should().Be(relative);
    }

    [Fact]
    public void Classify_OutsideBoundary_IsIgnored()
    {
        var full = Place("tool.execute.before.js");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: false, firstLine: "// @priority 7", contentHash: null);

        file.Disposition.Should().Be(HookFileDisposition.Ignored);
        file.RelativePath.Should().Be("tool.execute.before.js");
    }

    [Theory]
    [InlineData("// @priority 7", 7)]
    [InlineData("  // @priority -12  ", -12)]
    public void Classify_PriorityComment_SetsExplicitPriority(string line, int expected)
    {
        var full = Place("tool.execute.before.js");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, line, contentHash: null);

        file.Disposition.Should().Be(HookFileDisposition.Script);
        file.ExplicitPriority.Should().Be(expected);
    }

    [Theory]
    [InlineData("console.log(1)")]
    [InlineData("// @priority")]
    [InlineData("// @priority 7 extra")]
    [InlineData("")]
    public void Classify_NonPriorityFirstLine_LeavesExplicitPriorityNull(string line)
    {
        var full = Place("tool.execute.before.js");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, line, contentHash: null);

        file.Disposition.Should().Be(HookFileDisposition.Script);
        file.ExplicitPriority.Should().BeNull();
    }

    [Theory]
    [InlineData("chat.params.js")]
    [InlineData("Chat.Params.js")]
    public void Classify_LegacyPoint_IsIgnored(string name)
    {
        var full = Place(name);

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, firstLine: "// @priority 7", contentHash: null);

        file.Disposition.Should().Be(HookFileDisposition.Ignored);
    }

    [Fact]
    public void Classify_DllAtRoot_IsEntryDllWithCallerHash()
    {
        var full = Place("plugin.DLL");

        var file = HookDirectoryCatalog.Classify(_root, full, withinBoundary: true, firstLine: "// @priority 7", contentHash: "abc");

        file.Disposition.Should().Be(HookFileDisposition.EntryDll);
        file.Point.Should().BeNull();
        file.ContentHash.Should().Be("abc");
        file.ExplicitPriority.Should().BeNull();
        file.RelativePath.Should().Be("plugin.DLL");
    }

    [Fact]
    public void Diff_InsertEarlierScript_RecomputesOrdinalPrioritiesBelowTen()
    {
        var c = Place("tool.execute.before/c.js");
        var a = Place("tool.execute.before/a.js");
        var current = new List<ScriptBinding>
        {
            Binding(c, "tool.execute.before/c.js", "tool.execute.before", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, c, true, null, null),
            HookDirectoryCatalog.Classify(_root, a, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        var addedA = diff.AddScripts.Should().ContainSingle(s => s.RelativePath == "tool.execute.before/a.js").Subject;
        var addedC = diff.AddScripts.Should().ContainSingle(s => s.RelativePath == "tool.execute.before/c.js").Subject;
        addedA.Priority.Should().Be(-1000);
        addedC.Priority.Should().Be(-999);
        addedA.Priority.Should().BeLessThan(addedC.Priority);
        addedA.Priority.Should().BeLessThan(10);
        addedC.Priority.Should().BeLessThan(10);
        addedA.Point.Should().Be("tool.execute.before");
        addedA.Spec.Should().Be(HookRegistry.ToolExecuteBefore);
        diff.RemoveScriptPaths.Should().Contain(HookDirectoryCatalog.PathKey(c));
        diff.RemoveScriptPaths.Should().NotContain(HookDirectoryCatalog.PathKey(a));
    }

    [Fact]
    public void Diff_OrdinalPathOrder_PutsUppercaseBeforeLowercase()
    {
        var upper = Place("tool.execute.before/A.js");
        var lower = Place("tool.execute.before/b.js");
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, lower, true, null, null),
            HookDirectoryCatalog.Classify(_root, upper, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff([], [], snapshot);

        PriorityOf(diff, "tool.execute.before/A.js").Should().Be(-1000);
        PriorityOf(diff, "tool.execute.before/b.js").Should().Be(-999);
        PriorityOf(diff, "tool.execute.before/A.js")
            .Should().BeLessThan(PriorityOf(diff, "tool.execute.before/b.js"));
    }

    [Fact]
    public void Diff_AutoPriority_SortsRelativePathByOrdinal()
    {
        // Ordinal：'B' < 'a'。忽略大小写则会把 a.js 排在 B.js 前面。
        var upper = Place("tool.execute.before/B.js");
        var lower = Place("tool.execute.before/a.js");
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, lower, true, null, null),
            HookDirectoryCatalog.Classify(_root, upper, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff([], [], snapshot);

        PriorityOf(diff, "tool.execute.before/B.js").Should().Be(-1000);
        PriorityOf(diff, "tool.execute.before/a.js").Should().Be(-999);
    }

    [Fact]
    public void Diff_ExplicitPriority_DoesNotConsumeAutoIndex()
    {
        var a = Place("tool.execute.before/a.js");
        var b = Place("tool.execute.before/b.js");
        var c = Place("tool.execute.before/c.js");
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, b, true, "// @priority 50", null),
            HookDirectoryCatalog.Classify(_root, c, true, null, null),
            HookDirectoryCatalog.Classify(_root, a, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff([], [], snapshot);

        PriorityOf(diff, "tool.execute.before/a.js").Should().Be(-1000);
        PriorityOf(diff, "tool.execute.before/c.js").Should().Be(-999);
        PriorityOf(diff, "tool.execute.before/b.js").Should().Be(50);
        diff.AddScripts.Single(s => s.RelativePath == "tool.execute.before/b.js").Spec
            .Should().Be(HookRegistry.ToolExecuteBefore);
    }

    [Fact]
    public void Diff_PriorityLineChange_RemovesThenAddsWithNewNumber()
    {
        var full = Place("tool.execute.before.js");
        var current = new List<ScriptBinding>
        {
            Binding(full, "tool.execute.before.js", "tool.execute.before", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, "// @priority 7", null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        diff.RemoveScriptPaths.Should().Contain(HookDirectoryCatalog.PathKey(full));
        var added = diff.AddScripts.Should().ContainSingle().Subject;
        HookDirectoryCatalog.PathKey(added.FullPath).Should().Be(HookDirectoryCatalog.PathKey(full));
        added.Priority.Should().Be(7);
        added.Spec.Should().Be(HookPointNames.Resolve("tool.execute.before"));
    }

    [Fact]
    public void Diff_BodyChangeWithoutPriorityChange_TouchesNeitherScriptList()
    {
        var full = Place("tool.execute.before.js");
        var current = new List<ScriptBinding>
        {
            Binding(full, "tool.execute.before.js", "tool.execute.before", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, "module.exports = async () => ({ continue: true });", null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        diff.RemoveScriptPaths.Should().NotContain(HookDirectoryCatalog.PathKey(full));
        diff.AddScripts.Select(s => HookDirectoryCatalog.PathKey(s.FullPath))
            .Should().NotContain(HookDirectoryCatalog.PathKey(full));
        diff.RemoveScriptPaths.Should().BeEmpty();
        diff.AddScripts.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DllHashChange_RemovesThenAdds()
    {
        var full = Place("plugin.dll");
        var current = new List<DllBinding> { new(full, "aaa") };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, null, "bbb"),
        };

        var diff = HookDirectoryCatalog.Diff([], current, snapshot);

        diff.RemoveDllPaths.Should().Contain(HookDirectoryCatalog.PathKey(full));
        var added = diff.AddDlls.Should().ContainSingle().Subject;
        added.ContentHash.Should().Be("bbb");
        HookDirectoryCatalog.PathKey(added.FullPath).Should().Be(HookDirectoryCatalog.PathKey(full));
        diff.AddScripts.Should().BeEmpty();
        diff.RemoveScriptPaths.Should().BeEmpty();
    }

    [Fact]
    public void Diff_DllHashUnchanged_TouchesNeitherDllList()
    {
        var full = Place("plugin.dll");
        var current = new List<DllBinding> { new(full, "aaa") };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, null, "aaa"),
        };

        var diff = HookDirectoryCatalog.Diff([], current, snapshot);

        diff.RemoveDllPaths.Should().BeEmpty();
        diff.AddDlls.Should().BeEmpty();
    }

    [Fact]
    public void Diff_HashCompare_IsOrdinal()
    {
        var full = Place("plugin.dll");
        var current = new List<DllBinding> { new(full, "Ab") };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, null, "ab"),
        };

        var diff = HookDirectoryCatalog.Diff([], current, snapshot);

        diff.RemoveDllPaths.Should().Contain(HookDirectoryCatalog.PathKey(full));
        diff.AddDlls.Should().ContainSingle().Which.ContentHash.Should().Be("ab");
    }

    [Fact]
    public void Diff_MissingScriptAndDll_AreRemoved()
    {
        var script = Place("tool.execute.before.js");
        var dll = Place("plugin.dll");
        var scripts = new List<ScriptBinding>
        {
            Binding(script, "tool.execute.before.js", "tool.execute.before", -1000),
        };
        var dlls = new List<DllBinding> { new(dll, "aaa") };

        var diff = HookDirectoryCatalog.Diff(scripts, dlls, []);

        diff.RemoveScriptPaths.Should().ContainSingle().Which.Should().Be(HookDirectoryCatalog.PathKey(script));
        diff.RemoveDllPaths.Should().ContainSingle().Which.Should().Be(HookDirectoryCatalog.PathKey(dll));
        diff.AddScripts.Should().BeEmpty();
        diff.AddDlls.Should().BeEmpty();
    }

    [Fact]
    public void Diff_IgnoredFiles_ProduceNoBindings()
    {
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, Place("point/lib/a.js"), true, null, null),
            HookDirectoryCatalog.Classify(_root, Place("node_modules/a.js"), true, null, "hash"),
            HookDirectoryCatalog.Classify(_root, Place(".cache/a.dll"), true, null, "hash"),
        };

        var diff = HookDirectoryCatalog.Diff([], [], snapshot);

        diff.AddScripts.Should().BeEmpty();
        diff.AddDlls.Should().BeEmpty();
        diff.RemoveScriptPaths.Should().BeEmpty();
        diff.RemoveDllPaths.Should().BeEmpty();
    }

    [Fact]
    public void Diff_AutoPriority_RestartsPerPoint()
    {
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, Place("tool.execute.before/b.js"), true, null, null),
            HookDirectoryCatalog.Classify(_root, Place("llm.params.js"), true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff([], [], snapshot);

        diff.AddScripts.Should().HaveCount(2);
        diff.AddScripts.Should().OnlyContain(s => s.Priority == -1000);
        diff.AddScripts.Single(s => s.Point == "llm.params").Spec.Should().Be(HookRegistry.LlmParams);
    }

    [Fact]
    public void Diff_PointChange_RemovesThenAdds()
    {
        var full = Place("tool.execute.before.js");
        var current = new List<ScriptBinding>
        {
            Binding(full, "tool.execute.before.js", "other.point", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        diff.RemoveScriptPaths.Should().Contain(HookDirectoryCatalog.PathKey(full));
        var added = diff.AddScripts.Should().ContainSingle().Subject;
        added.Point.Should().Be("tool.execute.before");
        added.Priority.Should().Be(-1000);
    }

    [Fact]
    public void Diff_TrailingSeparator_MatchesSameScript()
    {
        var full = Place("tool.execute.before.js");
        var withSlash = full + Path.DirectorySeparatorChar;
        var current = new List<ScriptBinding>
        {
            Binding(withSlash, "tool.execute.before.js", "tool.execute.before", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, full, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        diff.RemoveScriptPaths.Should().BeEmpty();
        diff.AddScripts.Should().BeEmpty();
    }

    [Fact]
    public void PathKey_DropsTrailingSeparatorAfterFullPath()
    {
        var withSlash = _root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        var key = HookDirectoryCatalog.PathKey(withSlash);

        key.Should().Be(HookDirectoryCatalog.PathKey(_root));
        key.Should().NotEndWith(Path.DirectorySeparatorChar.ToString());
        key.Should().NotEndWith(Path.AltDirectorySeparatorChar.ToString());
    }

    [Fact]
    public void Diff_PathKey_FollowsPlatformComparison()
    {
        var full = Place("tool.execute.before/audit.js");
        var flipped = Path.Combine(_root, "tool.execute.before", "Audit.js");
        var current = new List<ScriptBinding>
        {
            Binding(full, "tool.execute.before/audit.js", "tool.execute.before", -1000),
        };
        var snapshot = new[]
        {
            HookDirectoryCatalog.Classify(_root, flipped, true, null, null),
        };

        var diff = HookDirectoryCatalog.Diff(current, [], snapshot);

        if (PathBoundary.PathComparison == StringComparison.OrdinalIgnoreCase)
        {
            diff.RemoveScriptPaths.Should().BeEmpty();
            diff.AddScripts.Should().BeEmpty();
        }
        else
        {
            diff.RemoveScriptPaths.Should().Contain(HookDirectoryCatalog.PathKey(full));
            diff.AddScripts.Should().ContainSingle().Which.RelativePath.Should().Be("tool.execute.before/Audit.js");
        }
    }

    private string Place(string relative)
    {
        var full = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, "module.exports = async () => ({ continue: true });\n");
        return full;
    }

    private static ScriptBinding Binding(string full, string relative, string point, int priority)
        => new(full, relative, point, priority, HookPointNames.Resolve(point));

    private static int PriorityOf(HookCatalogDiff diff, string relative)
        => diff.AddScripts.Single(s => s.RelativePath == relative).Priority;
}
