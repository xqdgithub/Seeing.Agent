# WebUI Review Issues

## Date: 2026-05-22

### High Priority Issues

1. **Missing Search/Filter on ToolsPage**
   - SkillsPage has search, but ToolsPage doesn't
   - Users cannot quickly find specific tools

2. **Missing Search/Filter on McpPage**
   - No way to filter servers when list grows

3. **Card Icons Using Text Instead of Icons**
   - SkillsPage uses "S" text for skill icons
   - ToolsPage uses emoji which is not professional
   - Should use AntDesign Icon components

4. **Missing Loading States**
   - Some pages have Spin but inconsistent implementation
   - Need skeleton loading for better UX

### Medium Priority Issues

1. **Missing Pagination**
   - All pages assume small datasets
   - Will break with hundreds of items

2. **Empty State Messages**
   - Empty component exists but could be more helpful
   - Should include action suggestions

3. **Session ID Truncation**
   - SessionsPage shows truncated UUIDs
   - Hard to distinguish between sessions
   - Should show title or allow rename

### Low Priority Issues

1. **Missing Tool Details View**
   - No way to see tool parameter schema
   - No documentation links

2. **Missing Server Health Metrics**
   - MCP page shows status but no latency
   - No last activity timestamp

3. **Missing Confirmation Dialogs**
   - Delete actions should have confirmation
   - SessionsPage has Popconfirm but could be improved
