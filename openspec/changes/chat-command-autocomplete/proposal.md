## Why

Users currently have no way to discover available commands in the chat interface. This leads to confusion about what actions are possible and requires users to memorize commands or refer to external documentation. An autocomplete system triggered by `/` would make commands discoverable and improve user productivity.

## What Changes

- Add command autocomplete dropdown triggered by `/` character in chat input
- Display all available commands in a filterable list
- Support keyboard navigation (arrow keys, enter to select)
- Show command descriptions and usage hints
- Insert selected command into input field
- Support fuzzy search/filtering of commands as user types after `/`

## Capabilities

### New Capabilities

- `command-autocomplete`: Autocomplete system for discovering and selecting chat commands via `/` trigger

### Modified Capabilities

None

## Impact

- **Frontend Components**: Chat input component, new autocomplete dropdown component
- **APIs**: Command registration/listing API endpoint (if not already present)
- **User Experience**: Improved command discoverability and faster command entry
- **Dependencies**: May require Ant Design Blazor components (AutoComplete, Select, or custom dropdown)
