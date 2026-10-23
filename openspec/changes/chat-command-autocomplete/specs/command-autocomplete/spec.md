## ADDED Requirements

### Requirement: Slash trigger detection
The system SHALL detect when the user types `/` in the chat input and trigger the command autocomplete dropdown.

#### Scenario: Trigger at line start
- **WHEN** user types `/` at the beginning of the input field
- **THEN** the autocomplete dropdown SHALL appear

#### Scenario: Trigger after whitespace
- **WHEN** user types `/` after a space or newline character
- **THEN** the autocomplete dropdown SHALL appear

#### Scenario: No trigger mid-word
- **WHEN** user types `/` in the middle of text without preceding whitespace
- **THEN** the autocomplete dropdown SHALL NOT appear

#### Scenario: Close on escape
- **WHEN** user presses Escape key while dropdown is open
- **THEN** the dropdown SHALL close without inserting any command

### Requirement: Command list display
The system SHALL display all available commands (tools and skills) in the autocomplete dropdown.

#### Scenario: Show all commands initially
- **WHEN** the autocomplete dropdown opens
- **THEN** all available tools and skills SHALL be displayed in alphabetical order

#### Scenario: Show command details
- **WHEN** the dropdown displays commands
- **THEN** each command item SHALL show the command name and description

#### Scenario: Distinguish tools and skills
- **WHEN** displaying the command list
- **THEN** tools and skills SHALL be visually distinguishable (by icon)

### Requirement: Filtering commands
The system SHALL filter the command list based on user input after the `/` trigger.

#### Scenario: Filter by substring
- **WHEN** user types characters after `/` (e.g., `/fil`)
- **THEN** only commands containing "fil" in their name or description SHALL be displayed

#### Scenario: Case-insensitive filtering
- **WHEN** user types `/FILE` or `/file`
- **THEN** the same filtered results SHALL be displayed

#### Scenario: No results message
- **WHEN** no commands match the filter text
- **THEN** a "No commands found" message SHALL be displayed

### Requirement: Keyboard navigation
The system SHALL support keyboard navigation within the autocomplete dropdown.

#### Scenario: Navigate down
- **WHEN** user presses Down arrow key while dropdown is open
- **THEN** the next item in the list SHALL be highlighted

#### Scenario: Navigate up
- **WHEN** user presses Up arrow key while dropdown is open
- **THEN** the previous item in the list SHALL be highlighted

#### Scenario: Wrap navigation
- **WHEN** user presses Down arrow on the last item
- **THEN** the first item SHALL be highlighted
- **AND** when user presses Up arrow on the first item, the last item SHALL be highlighted

#### Scenario: Select with Enter
- **WHEN** user presses Enter key while an item is highlighted
- **THEN** the selected command SHALL be inserted into the input field
- **AND** the message SHALL NOT be sent

#### Scenario: Select with Tab
- **WHEN** user presses Tab key while an item is highlighted
- **THEN** the selected command SHALL be inserted into the input field
- **AND** focus SHALL remain on the textarea for parameter entry

### Requirement: Mouse interaction
The system SHALL support mouse interaction with the autocomplete dropdown.

#### Scenario: Hover highlight
- **WHEN** user hovers over a command item with the mouse
- **THEN** that item SHALL be highlighted

#### Scenario: Click to select
- **WHEN** user clicks on a command item
- **THEN** the command SHALL be inserted into the input field
- **AND** the dropdown SHALL close

### Requirement: Command insertion
The system SHALL insert the selected command into the input field correctly.

#### Scenario: Replace trigger and filter text
- **WHEN** user selects a command after typing `/fil`
- **THEN** `/fil` SHALL be replaced with the full command (e.g., `/file-system`)

#### Scenario: Preserve surrounding text
- **WHEN** user selects a command while there is text before or after the trigger
- **THEN** surrounding text SHALL be preserved
- **AND** the command SHALL be inserted at the cursor position

#### Scenario: Add space after command
- **WHEN** a command is inserted
- **THEN** a space SHALL be added after the command for easy parameter entry

### Requirement: Dropdown dismiss
The system SHALL dismiss the dropdown appropriately based on user actions.

#### Scenario: Dismiss on selection
- **WHEN** user selects a command (via Enter, Tab, or click)
- **THEN** the dropdown SHALL close

#### Scenario: Dismiss on outside click
- **WHEN** user clicks outside the dropdown
- **THEN** the dropdown SHALL close

#### Scenario: Dismiss on trigger deletion
- **WHEN** user deletes the `/` character
- **THEN** the dropdown SHALL close

#### Scenario: Keep open while navigating
- **WHEN** user uses arrow keys to navigate
- **THEN** the dropdown SHALL remain open

### Requirement: Scrolling behavior
The system SHALL handle long command lists appropriately.

#### Scenario: Maximum height
- **WHEN** the command list exceeds 300px height
- **THEN** the dropdown SHALL become scrollable

#### Scenario: Scroll to highlighted item
- **WHEN** user navigates with arrow keys
- **THEN** the dropdown SHALL scroll to keep the highlighted item visible

### Requirement: Multi-line support
The system SHALL work correctly in multi-line input scenarios.

#### Scenario: Trigger on new line
- **WHEN** user types `/` at the start of a new line (after Enter)
- **THEN** the autocomplete dropdown SHALL appear

#### Scenario: Positioning in multi-line
- **WHEN** the cursor is on a line other than the first
- **THEN** the dropdown SHALL be positioned near the cursor on that line

### Requirement: Existing feature compatibility
The system SHALL preserve all existing textarea functionality.

#### Scenario: Auto-resize still works
- **WHEN** user types multiple lines of text
- **THEN** the textarea SHALL auto-resize as before

#### Scenario: Paste image still works
- **WHEN** user pastes an image while the dropdown is open
- **THEN** the image SHALL be attached as expected

#### Scenario: Drag-drop still works
- **WHEN** user drags a file onto the input area
- **THEN** the file SHALL be attached as expected

#### Scenario: Send message still works
- **WHEN** user presses Enter while dropdown is NOT open
- **THEN** the message SHALL be sent as before

### Requirement: Focus management
The system SHALL maintain appropriate focus on the textarea.

#### Scenario: Focus after command selection
- **WHEN** user selects a command via Enter, Tab, or click
- **THEN** focus SHALL remain on the textarea for parameter entry

#### Scenario: Focus after Escape
- **WHEN** user presses Escape to close dropdown
- **THEN** focus SHALL remain on the textarea

### Requirement: QuickCommandPopover coexistence
The system SHALL work alongside the existing QuickCommandPopover.

#### Scenario: Autocomplete takes precedence
- **WHEN** QuickCommandPopover is open and user types `/`
- **THEN** QuickCommandPopover SHALL close
- **AND** the autocomplete dropdown SHALL appear

#### Scenario: Both mechanisms available
- **WHEN** user wants to insert a command
- **THEN** both the `/` autocomplete and the QuickCommandPopover button SHALL be available
