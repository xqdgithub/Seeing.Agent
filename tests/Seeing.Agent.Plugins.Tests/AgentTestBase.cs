using FluentAssertions;

namespace Seeing.Agent.Plugins.Tests
{
    // Base test utilities for agent tests
    public class AgentTestBase
    {
        // CreateAgent<T> helper (simple parameterless constructor)
        protected T CreateAgent<T>() where T : class, new()
        {
            return new T();
        }

        // Assert common agent properties: Name, Mode, Description
        protected void AssertAgentProperties(object agent, string name, string mode, string description)
        {
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            var type = agent.GetType();

            // Try to locate common property names
            var nameProp = new[] { "Name", "AgentName", "NameValue" }
                .Select(p => type.GetProperty(p))
                .FirstOrDefault(p => p != null);
            if (nameProp != null)
            {
                var val = nameProp.GetValue(agent) as string;
                val.Should().Be(name);
            }

            var modeProp = new[] { "Mode", "ExecutionMode", "AgentMode" }
                .Select(p => type.GetProperty(p))
                .FirstOrDefault(p => p != null);
            if (modeProp != null)
            {
                var val = modeProp.GetValue(agent) as string;
                val.Should().Be(mode);
            }

            var descProp = new[] { "Description", "DescriptionText", "Info" }
                .Select(p => type.GetProperty(p))
                .FirstOrDefault(p => p != null);
            if (descProp != null)
            {
                var val = descProp.GetValue(agent) as string;
                val.Should().Be(description);
            }
        }

        // AssertTools: verify allowed and denied tools against agent's declared tools
        protected void AssertTools(object agent, IEnumerable<string> allowedTools, IEnumerable<string> deniedTools)
        {
            if (agent == null) throw new ArgumentNullException(nameof(agent));
            var type = agent.GetType();

            // Try to read common tools collection
            IEnumerable<string>? toolsVal = null;
            var possibleProps = new[] { "Tools", "AvailableTools", "ToolNames", "SupportedTools" };
            foreach (var propName in possibleProps)
            {
                var prop = type.GetProperty(propName);
                if (prop != null)
                {
                    var value = prop.GetValue(agent);
                    if (value is IEnumerable<string> enumerable)
                    {
                        toolsVal = enumerable;
                        break;
                    }
                    if (value is IEnumerable<object> objEnum)
                    {
                        toolsVal = objEnum.Select(o => o?.ToString()).Where(s => s != null)!;
                        break;
                    }
                }
            }

            // If no tools collection found, treat as empty
            var tools = toolsVal ?? Enumerable.Empty<string>();

            if (allowedTools != null)
            {
                foreach (var t in allowedTools)
                {
                    tools.Should().Contain(t);
                }
            }
            if (deniedTools != null)
            {
                foreach (var t in deniedTools)
                {
                    tools.Should().NotContain(t);
                }
            }
        }
    }
}
