using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Seeing.Agent.Core.Interfaces;
using Seeing.Agent.Rules;
using Xunit;

namespace Seeing.Agent.Tests.Rules
{
    /// <summary>
    /// RuleEngine 单元测试
    /// </summary>
    public class RuleEngineTests
    {
        private readonly Mock<ILogger<RuleEngine>> _loggerMock;
        private readonly RuleEngine _engine;

        public RuleEngineTests()
        {
            _loggerMock = new Mock<ILogger<RuleEngine>>();
            _engine = new RuleEngine(_loggerMock.Object);
        }

        [Fact]
        public void AddRule_ShouldStoreRule()
        {
            var rule = new PermissionRule
            {
                Permission = "file_read",
                Pattern = "*",
                Action = PermissionAction.Allow
            };

            _engine.AddRule(rule);

            _engine.GetRules().Should().Contain(rule);
        }

        [Fact]
        public void Evaluate_ShouldReturnAllow_WhenRuleAllows()
        {
            _engine.AddRule(new PermissionRule
            {
                Permission = "file_read",
                Pattern = "/safe/*",
                Action = PermissionAction.Allow
            });

            var result = _engine.Evaluate("file_read", "/safe/test.txt");

            result.Should().Be(PermissionAction.Allow);
        }

        [Fact]
        public void Evaluate_ShouldReturnDeny_WhenRuleDenies()
        {
            _engine.AddRule(new PermissionRule
            {
                Permission = "file_write",
                Pattern = "/system/*",
                Action = PermissionAction.Deny
            });

            var result = _engine.Evaluate("file_write", "/system/config.txt");

            result.Should().Be(PermissionAction.Deny);
        }

        [Fact]
        public void Evaluate_ShouldReturnAllow_WhenNoMatchingRule()
        {
            // 默认行为：未匹配规则时返回 Allow
            var result = _engine.Evaluate("file_delete", "/unknown/path.txt");

            result.Should().Be(PermissionAction.Allow);
        }

        [Fact]
        public void MergeRules_ShouldCombineMultipleRules()
        {
            var rules = new[]
            {
                new PermissionRule { Permission = "a", Pattern = "*", Action = PermissionAction.Allow },
                new PermissionRule { Permission = "b", Pattern = "*", Action = PermissionAction.Deny }
            };

            _engine.MergeRules(rules);

            _engine.GetRules().Should().HaveCount(2);
        }

        [Fact]
        public void IsToolDisabled_ShouldReturnTrue_WhenToolDenied()
        {
            _engine.AddRule(new PermissionRule
            {
                Permission = "tool",
                Pattern = "dangerous_tool",
                Action = PermissionAction.Deny
            });

            var result = _engine.IsToolDisabled("dangerous_tool");

            result.Should().BeTrue();
        }

        [Fact]
        public void Evaluate_ShouldMatchWildcardPattern()
        {
            _engine.AddRule(new PermissionRule
            {
                Permission = "file_read",
                Pattern = "/data/**/*.txt",
                Action = PermissionAction.Allow
            });

            var result = _engine.Evaluate("file_read", "/data/subfolder/file.txt");

            result.Should().Be(PermissionAction.Allow);
        }

        [Fact]
        public void Evaluate_ShouldPrioritizeLongerPattern()
        {
            // 更长/更具体的模式优先
            _engine.AddRule(new PermissionRule
            {
                Permission = "file",
                Pattern = "/safe/*",
                Action = PermissionAction.Allow
            });
            _engine.AddRule(new PermissionRule
            {
                Permission = "file",
                Pattern = "/safe/secret/*",
                Action = PermissionAction.Deny
            });

            var result = _engine.Evaluate("file", "/safe/secret/file.txt");

            result.Should().Be(PermissionAction.Deny);
        }
    }
}