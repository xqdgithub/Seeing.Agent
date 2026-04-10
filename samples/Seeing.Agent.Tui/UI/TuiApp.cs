using Terminal.Gui;
using Seeing.Agent.Commands;
using Seeing.Agent.Tui.Core;
using Seeing.Agent.Tui.Services;

namespace Seeing.Agent.Tui.UI;

/// <summary>
/// Terminal.Gui v1 主应用 - 使用英文避免 UTF-8 bug
/// </summary>
public class TuiApp
{
    private readonly TuiState _state;
    private readonly ChatOrchestrator _orchestrator;
    private readonly CommandDispatcher _commandDispatcher;

    // UI 组件
    private Window _mainWindow = null!;
    private ListView _messageList = null!;
    private TextField _inputField = null!;
    private Label _statusBar = null!;
    private Label _streamingLabel = null!;

    // 消息数据
    private readonly List<string> _displayMessages = new();

    public TuiApp(TuiState state, ChatOrchestrator orchestrator, CommandDispatcher commandDispatcher)
    {
        _state = state;
        _orchestrator = orchestrator;
        _commandDispatcher = commandDispatcher;
    }

    /// <summary>
    /// Run TUI application
    /// </summary>
    public void Run()
    {
        Application.Init();

        _mainWindow = new Window("Seeing.Agent")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _statusBar = new Label(BuildStatusBarText())
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        _mainWindow.Add(_statusBar);

        var messageFrame = new FrameView("Messages")
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = Dim.Fill() - 4
        };

        _messageList = new ListView(_displayMessages)
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            AllowsMarking = false,
            AllowsMultipleSelection = false
        };
        messageFrame.Add(_messageList);
        _mainWindow.Add(messageFrame);

        _streamingLabel = new Label("")
        {
            X = 0,
            Y = Pos.Bottom(messageFrame),
            Width = Dim.Fill(),
            Height = 1,
            Visible = false
        };
        _mainWindow.Add(_streamingLabel);

        var inputFrame = new FrameView("Input")
        {
            X = 0,
            Y = Pos.Bottom(messageFrame) + 1,
            Width = Dim.Fill(),
            Height = 3
        };

        _inputField = new TextField("")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 1
        };
        _inputField.KeyDown += OnInputKeyDown;
        inputFrame.Add(_inputField);
        _mainWindow.Add(inputFrame);

        AddWelcomeMessage();
        _inputField.SetFocus();

        Application.Top.Add(_mainWindow);
        Application.Run();
        Application.Shutdown();
    }

    private string BuildStatusBarText()
    {
        return $"Agent: {_state.CurrentAgentKey} | Model: {_state.CurrentModel ?? "default"} | Tools:{_state.ToolCount} Skills:{_state.SkillCount} MCP:{_state.McpServerCount} | Msgs:{_state.Messages.Count}";
    }

    private void AddWelcomeMessage()
    {
        _displayMessages.Add("========== Seeing.Agent TUI ==========");
        _displayMessages.Add("");
        _displayMessages.Add("Commands:");
        _displayMessages.Add("  /help          Show help");
        _displayMessages.Add("  /agents        List agents");
        _displayMessages.Add("  /agent <name>  Switch agent");
        _displayMessages.Add("  /models        List models");
        _displayMessages.Add("  /tools         Show tools");
        _displayMessages.Add("  /exit          Exit program");
        _displayMessages.Add("");
        _displayMessages.Add("----------------------------------------");
        UpdateMessageList();
    }

    private void OnInputKeyDown(View.KeyEventEventArgs e)
    {
        if (e.KeyEvent.Key == Key.Enter)
        {
            var input = _inputField.Text.ToString()?.Trim();
            if (!string.IsNullOrEmpty(input))
            {
                ProcessInput(input);
                _inputField.Text = "";
            }
            e.Handled = true;
        }
    }

    private void ProcessInput(string input)
    {
        _displayMessages.Add($"> {input}");
        UpdateMessageList();

        if (input.StartsWith('/'))
        {
            ProcessCommand(input);
        }
        else
        {
            ProcessChat(input);
        }
    }

    private void ProcessCommand(string input)
    {
        var context = new CommandContext
        {
            SessionId = _state.SessionId,
            WorkspaceRoot = _state.WorkspaceRoot
        };
        var result = _commandDispatcher.HandleAsync(input, context, CancellationToken.None).Result;

        if (result.ShouldExit)
        {
            Application.RequestStop();
            return;
        }

        if (!string.IsNullOrEmpty(result.Message))
        {
            var lines = result.Message.Split('\n');
            foreach (var line in lines)
            {
                var cleanLine = System.Text.RegularExpressions.Regex.Replace(line, @"\[[^\]]+\]", "");
                _displayMessages.Add(cleanLine);
            }
            UpdateMessageList();
        }

        _statusBar.Text = BuildStatusBarText();
    }

    private void ProcessChat(string input)
    {
        _state.IsProcessing = true;
        UpdateStatusBar();
        _streamingLabel.Text = "Processing...";
        _streamingLabel.Visible = true;
        Application.MainLoop.Invoke(() => { });

        try
        {
            _orchestrator.RunTurnAsync(input, CancellationToken.None).Wait();

            var lastMessage = _state.Messages.LastOrDefault();
            if (lastMessage != null && lastMessage.Role == "assistant")
            {
                _displayMessages.Add($"[AI] {TruncateSafe(lastMessage.Content)}");
                UpdateMessageList();
            }
        }
        catch (Exception ex)
        {
            _displayMessages.Add($"[Error] {ex.Message}");
            UpdateMessageList();
        }
        finally
        {
            _state.IsProcessing = false;
            _streamingLabel.Visible = false;
            _statusBar.Text = BuildStatusBarText();
        }
    }

    private void UpdateStatusBar()
    {
        _statusBar.Text = BuildStatusBarText();
    }

    private void UpdateMessageList()
    {
        _messageList.SetSource(_displayMessages);
        _messageList.SelectedItem = _displayMessages.Count - 1;
    }

    private string TruncateSafe(string text)
    {
        // Truncate to avoid Terminal.Gui v1 UTF-8 bug
        const int maxLen = 100;
        if (text.Length > maxLen)
            return text.Substring(0, maxLen) + "...";
        return text;
    }
}