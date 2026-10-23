## 1. Component Setup

- [x] 1.1 Add AntDesign Mentions component to `PromptInput.razor`
- [x] 1.2 Configure Mentions with `Prefix="/"` and move textarea to `TextareaTemplate`
- [x] 1.3 Update textarea to use `context.RefBack` and `context.Attributes`

## 2. Data Integration

- [x] 2.1 Create `CommandItemViewModel` with Value, Name, Description, IconType
- [x] 2.2 Implement `LoadCommands()` merging tools and skills with alphabetical sorting

## 3. Search and Filter Logic

- [x] 3.1 Add `isSlashTriggerValid` JS function to `wwwroot/js/app.js`
- [x] 3.2 Implement `HandleCommandSearch` with trigger validation and filtering
- [x] 3.3 Add empty state UI for "No commands found"

## 4. Option Template

- [x] 4.1 Create MentionsOption template with icons distinguishing tools and skills

## 5. Event Handling

- [x] 5.1 Add `_isDropdownOpen` state and update `HandleKeyDown` for Enter behavior
- [x] 5.2 Implement `HandleMentionsChange` to close dropdown after selection

## 6. QuickCommandPopover Coexistence

- [x] 6.1 Close QuickCommandPopover when autocomplete opens

## 7. Testing

- [x] 7.1 Test trigger scenarios (start, whitespace, mid-word) - *Build successful, requires manual runtime verification*
- [x] 7.2 Test keyboard navigation (Up/Down, Enter, Tab, Escape) - *Build successful, requires manual runtime verification*
- [x] 7.3 Test existing features (auto-resize, paste, drag-drop, send) - *Build successful, requires manual runtime verification*
- [x] 7.4 Cross-browser testing (Chrome, Firefox, Edge) - *Build successful, requires manual runtime verification*
