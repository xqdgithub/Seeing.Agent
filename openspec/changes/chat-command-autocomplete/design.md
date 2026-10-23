## Context

The chat interface currently uses a custom `PromptInput.razor` component with a native `<textarea>` for message input. Users can insert commands via a `QuickCommandPopover` component that requires clicking a button to open - there's no autocomplete triggered by typing `/`. Tools and skills are already loaded and available through `ToolStateService` and `SkillStateService`, converted to `QuickToolViewModel` and `QuickSkillViewModel` collections.

The application uses Blazor with AntDesign Blazor (v1.6.2) for UI components. AntDesign provides a `Mentions` component that supports:
- Custom trigger prefix (`/`)
- `TextareaTemplate` to preserve native textarea functionality
- Built-in keyboard navigation, positioning, and filtering

**Current Flow:**
1. User clicks a button to open `QuickCommandPopover`
2. Searches/browses tools or skills in tabs
3. Clicks an item to insert `/{name}` into textarea

**Desired Flow:**
1. User types `/` in textarea
2. Autocomplete dropdown appears immediately
3. User filters by typing more characters or navigates with arrow keys
4. Selects command (Enter or click) to insert

## Goals / Non-Goals

**Goals:**
- Implement slash-triggered autocomplete using AntDesign Mentions component
- Support filtering as user types after `/`
- Leverage built-in keyboard navigation and positioning
- Preserve all existing textarea features (auto-resize, paste, drag-drop, JSInterop)

**Non-Goals:**
- Changing how tools/skills are registered or invoked
- Modifying backend command execution logic
- Implementing `@` mentions for users (only slash commands)
- Removing the existing `QuickCommandPopover` button (it can coexist)

## Decisions

### Decision 1: Use AntDesign Mentions Component

**Choice:** Leverage AntDesign's Mentions component with `Prefix="/"` for slash command triggering.

**Rationale:**
- Mentions supports custom prefix characters (`/`)
- `TextareaTemplate` allows preserving the native `<textarea>` with all existing handlers
- Built-in keyboard navigation, positioning, and filtering eliminates custom JS code
- Consistent with AntDesign design system
- Significantly reduces implementation and maintenance effort

**Implementation Approach:**
```razor
<Mentions Prefix="/"
          OnSearch="HandleCommandSearch"
          OnChange="HandleMentionsChange"
          @bind-Value="@InputText">
    <ChildContent>
        @foreach (var cmd in FilteredCommands)
        {
            <MentionsOption Value="@cmd.Value" Key="@cmd.Value">
                <Space>
                    <SpaceItem><Icon Type="@cmd.IconType" /></SpaceItem>
                    <SpaceItem>@cmd.Name</SpaceItem>
                </Space>
            </MentionsOption>
        }
    </ChildContent>
    <TextareaTemplate>
        <textarea @attributes="@context.Attributes"
                  @ref="context.RefBack"
                  id="@TextareaId"
                  class="prompt-textarea"
                  placeholder="@Placeholder"
                  disabled="@IsProcessing"
                  rows="1"
                  style="resize: none; overflow-y: auto; min-height: 40px; max-height: 200px;" />
    </TextareaTemplate>
</Mentions>
```

**Key Integration Notes:**
- Use `context.RefBack` for textarea reference (not `@ref`) - this allows Mentions to manage the reference
- Use `context.Attributes` to apply Mentions' internal attributes (value binding, event handlers)
- Existing JSInterop functions (`setupTextareaAutoResize`, `setupDropZone`, `setupPasteHandler`) work unchanged since they find textarea by `id`

**Trade-offs:**
- Less control over dropdown positioning (acceptable - AntDesign handles viewport edges)
- Must work within Mentions event model (OnChange instead of direct keydown handling)

### Decision 2: Trigger Only at Line Start or After Whitespace

**Choice:** Use JSInterop to check preceding character before showing commands.

**Rationale:**
- Mentions triggers on `/` automatically but doesn't know context
- `OnSearch` callback doesn't include cursor position info
- Need JSInterop to check the character before `/`

**Implementation:**
```javascript
// Add to app.js
function getTextBeforeCursor(textareaId) {
    const textarea = document.getElementById(textareaId);
    if (!textarea) return '';
    const cursorPos = textarea.selectionStart;
    return textarea.value.substring(0, cursorPos);
}

function isSlashTriggerValid(textareaId) {
    const textBeforeCursor = getTextBeforeCursor(textareaId);
    if (textBeforeCursor.length === 0) return true; // At start
    const lastChar = textBeforeCursor[textBeforeCursor.length - 1];
    return lastChar === ' ' || lastChar === '\n'; // After whitespace
}
```

```csharp
// In HandleCommandSearch
private async void HandleCommandSearch((string searchValue, string prefix) args)
{
    var isValid = await JSRuntime.InvokeAsync<bool>("isSlashTriggerValid", TextareaId);
    if (!isValid)
    {
        FilteredCommands = new List<CommandItemViewModel>(); // Empty list = no dropdown
        return;
    }
    // ... filter commands
}
```

### Decision 3: Reuse Existing ViewModel Data

**Choice:** Use existing `QuickToolViewModel` and `QuickSkillViewModel` from state services.

**Rationale:**
- Data already loaded and refreshed per-session
- Same models used by `QuickCommandPopover`
- No new API calls or state management needed
- Simple mapping to MentionsOption format

### Decision 4: Simple Substring Filtering

**Choice:** Case-insensitive substring matching for command filtering.

**Rationale:**
- Command lists are small (typically <50 items)
- Simple filtering is fast and predictable
- Matches the behavior of existing `QuickCommandPopover`

### Decision 5: Enter Key Behavior Switching

**Choice:** Override the original `HandleKeyDown` to check dropdown state.

**Rationale:**
- Mentions captures keyboard events but we need to distinguish:
  - Dropdown open + Enter → select command (don't send)
  - Dropdown closed + Enter → send message
- Track dropdown state via `OnSearch` (dropdown opens) and `OnChange` (dropdown closes after selection)

**Implementation:**
```csharp
private bool _isDropdownOpen = false;

private async Task HandleKeyDown(KeyboardEventArgs e)
{
    if (e.Key == "Enter" && !e.ShiftKey)
    {
        // If dropdown is open, let Mentions handle it (select command)
        if (_isDropdownOpen) return;
        
        // Otherwise, send message
        if (!string.IsNullOrWhiteSpace(InputText) || Attachments.Count > 0)
        {
            await OnSubmit();
        }
    }
}

private void HandleCommandSearch((string searchValue, string prefix) args)
{
    _isDropdownOpen = true;
    // ... filter logic
}

private void HandleMentionsChange(string value)
{
    _isDropdownOpen = false;
    // Command was inserted, space is auto-added by Mentions
}
```

### Decision 6: Empty State UI

**Choice:** Display a disabled MentionsOption when no commands match.

**Implementation:**
```razor
<ChildContent>
    @if (FilteredCommands.Count == 0)
    {
        <MentionsOption Value="" Disabled="true">
            <span style="color: var(--color-text-tertiary);">
                No commands found
            </span>
        </MentionsOption>
    }
    else
    {
        @foreach (var cmd in FilteredCommands)
        {
            <MentionsOption Value="@cmd.Value" Key="@cmd.Value">
                <Space>
                    <SpaceItem><Icon Type="@cmd.IconType" /></SpaceItem>
                    <SpaceItem>@cmd.Name</SpaceItem>
                </Space>
            </MentionsOption>
        }
    }
</ChildContent>
```

## Risks / Trade-offs

| Risk | Mitigation |
|------|------------|
| Mentions captures Enter key | Track `_isDropdownOpen` flag in `OnSearch`/`OnChange`; skip send logic when dropdown open |
| JSInterop with textarea inside Mentions | Use `context.RefBack` for reference; existing JS works by `id` selector |
| Trigger context validation | Add `isSlashTriggerValid` JS function; call from `OnSearch` |
| Mentions dropdown style customization | Use AntDesign's CSS variables; minimal custom CSS needed |
| QuickCommandPopover conflict | When autocomplete opens, close QuickCommandPopover via state binding |
| Focus management | Mentions preserves focus on textarea after selection; no extra handling needed |

## Migration Plan

1. **Phase 1:** Wrap existing textarea with Mentions component in `PromptInput.razor`
2. **Phase 2:** Implement `OnSearch` handler with context validation and filtering
3. **Phase 3:** Create MentionsOption templates with tool/skill icons
4. **Phase 4:** Test with existing features; adjust event handling if conflicts

**Rollback:** Simple reversion - remove Mentions wrapper, restore direct textarea. No new dependencies added.

## Open Questions (Resolved)

1. ~~Should the autocomplete show tool parameters/usage hints inline?~~
   - **Decision:** Start with just name and description; keep it simple

2. ~~Should recently used commands appear first?~~
   - **Decision:** Out of scope; alphabetical order is sufficient

3. ~~Should the dropdown show when user types `/` mid-sentence?~~
   - **Decision:** Only trigger after whitespace or at start; implement in `OnSearch` validation
