using Seeing.Agent.Abstractions.Todo;
using Seeing.Agent.Core.Todo;
using Xunit;

namespace Seeing.Agent.Tests.Core.Todo;

public class TodoListExtensionsTests
{
    [Fact]
    public void IsEmpty_ReturnsTrue_WhenNoItems()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem>() };
        Assert.True(list.IsEmpty());
    }

    [Fact]
    public void IsEmpty_ReturnsFalse_WhenHasItems()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "test", Status = TodoStatus.Pending }
        }};
        Assert.False(list.IsEmpty());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_DetectsPending()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "t1", Status = TodoStatus.Pending }
        }};
        Assert.True(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_DetectsInProgress()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "t1", Status = TodoStatus.InProgress }
        }};
        Assert.True(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasIncompletePendingOrInProgress_IgnoresCompletedCancelledPaused()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "t1", Status = TodoStatus.Completed },
            new() { Content = "t2", Status = TodoStatus.Cancelled },
            new() { Content = "t3", Status = TodoStatus.Paused }
        }};
        Assert.False(list.HasIncompletePendingOrInProgress());
    }

    [Fact]
    public void HasPaused_ReturnsTrue_WhenPausedExists()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "t1", Status = TodoStatus.Paused }
        }};
        Assert.True(list.HasPaused());
    }

    [Fact]
    public void HasPaused_ReturnsFalse_WhenNoPaused()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "t1", Status = TodoStatus.Pending }
        }};
        Assert.False(list.HasPaused());
    }

    [Fact]
    public void FormatBrief_IncludesAllStatusMarks()
    {
        var list = new TodoList { SessionId = "s1", Items = new List<TodoItem> {
            new() { Content = "pending task", Status = TodoStatus.Pending },
            new() { Content = "in progress task", Status = TodoStatus.InProgress },
            new() { Content = "done task", Status = TodoStatus.Completed },
            new() { Content = "cancelled task", Status = TodoStatus.Cancelled },
            new() { Content = "waiting task", Status = TodoStatus.Paused },
        }};
        var result = list.FormatBrief();
        Assert.Contains("[ ]", result);
        Assert.Contains("[▶]", result);
        Assert.Contains("[✔]", result);
        Assert.Contains("[✗]", result);
        Assert.Contains("[⏸]", result);
    }
}
