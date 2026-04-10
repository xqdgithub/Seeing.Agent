SessionView.cs created for NewTUI Session UI.
- Path: src/Seeing.Agent.NewTui/Views/SessionView.cs
- Implements: MessageList, input TextView, status Label; basic Enter/Esc/Ctrl-C handling
- Hooks: _state.StateChanged to refresh status; Application.Top.KeyPress to capture keys
- Validation: Requires 'dotnet build src/Seeing.Agent.NewTui' in a functioning environment

Notes:
- Ensure MessageList component is available and compatible with current Terminal.Gui version
- Consider accessibility and focus management in future iterations
- No sidebar or multi-window layout per requirements
