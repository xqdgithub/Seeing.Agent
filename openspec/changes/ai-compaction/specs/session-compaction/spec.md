## ADDED Requirements

### Requirement: AI-powered session compaction
The system SHALL provide an AI-powered session compaction service that generates structured summaries of conversation history, preserving key context while reducing message count.

#### Scenario: Successful compaction with sufficient messages
- **WHEN** user triggers `/compact` command on a session with more than 10 messages
- **THEN** system calls LLM to generate a structured summary
- **AND** system creates a summary message with `is_compaction_summary` metadata marker
- **AND** system replaces compacted messages with summary message
- **AND** system displays success notification with compressed count

#### Scenario: Compaction skipped due to insufficient messages
- **WHEN** user triggers `/compact` command on a session with 10 or fewer messages
- **THEN** system displays "消息数量较少，无需压缩" notification
- **AND** no changes are made to the session

#### Scenario: Compaction fails with fallback
- **WHEN** LLM call fails during compaction
- **THEN** system falls back to `SessionCompressor` sliding window truncation
- **AND** system displays warning notification about fallback
- **AND** system inserts system message noting truncation count

### Requirement: Compaction uses cost-effective model
The system SHALL prioritize cost-effective LLM models for compaction, selecting from a preference list: gpt-4o-mini, gpt-3.5-turbo, claude-3-haiku, deepseek-chat.

#### Scenario: Preferred model available
- **WHEN** compaction is triggered
- **AND** gpt-4o-mini model is configured
- **THEN** system uses gpt-4o-mini for summary generation

#### Scenario: Preferred models unavailable
- **WHEN** compaction is triggered
- **AND** no preferred models are configured
- **THEN** system uses first available model from LLM service

#### Scenario: No models configured
- **WHEN** compaction is triggered
- **AND** no models are available
- **THEN** system throws InvalidOperationException with message "LLM 服务没有配置任何可用模型"

### Requirement: Summary message preserves key information
The system SHALL generate summaries containing: user goals, key decisions, work in progress, open issues, and relevant files.

#### Scenario: Summary contains all sections
- **WHEN** LLM generates summary
- **THEN** summary includes sections for goals, decisions, progress, issues, and files
- **AND** sections use concise bullet points
- **AND** file paths and critical context are preserved exactly

### Requirement: Summary messages are visually distinct
The system SHALL render compaction summary messages with special UI treatment to distinguish them from regular messages.

#### Scenario: Summary message displayed
- **WHEN** session contains a message with `is_compaction_summary` metadata
- **THEN** message renders with an info alert style
- **AND** message shows compression count in header

### Requirement: Compaction state is tracked
The system SHALL track compaction progress to prevent concurrent compaction attempts.

#### Scenario: Compaction in progress
- **WHEN** compaction is being executed
- **THEN** `SessionState.IsCompacting` is set to true
- **AND** UI shows loading indicator

#### Scenario: Compaction completes
- **WHEN** compaction finishes (success or failure)
- **THEN** `SessionState.IsCompacting` is set to false
- **AND** UI loading indicator is hidden