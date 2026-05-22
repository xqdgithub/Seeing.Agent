# WebUI Review Learnings

## Date: 2026-05-22

### UI Design Patterns
- Dark theme with purple accent color works well for developer tools
- Sidebar + content layout pattern is consistent and effective
- Card grid view is good for displaying items with visual distinction
- Table view is good for data-dense information display

### AntDesign Components Used
- Breadcrumb for navigation context
- Button with icons for actions
- Card for item display
- Table for data listing
- Tag for status indicators
- Switch for toggle controls
- Input with prefix icons for search
- Empty for empty state
- Spin for loading state

### CSS Architecture
- CSS variables for theming (--color-*, --space-*, --font-*)
- Responsive breakpoints at 1024px, 768px, 480px
- Dark theme adjustments using [data-theme="dark"] selector
