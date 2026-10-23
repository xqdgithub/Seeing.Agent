## MODIFIED Requirements

### Requirement: Gateway orchestrator implementation
The GatewayOrchestrator SHALL use IChatOrchestrator internally for chat execution.

#### Scenario: Gateway delegates to ChatOrchestrator
- **WHEN** GatewayOrchestrator.ExecuteChatAsync is called with GatewayRequest
- **THEN** the system SHALL convert GatewayRequest to ChatInput/ChatOptions and call IChatOrchestrator.ExecuteAsync

#### Scenario: Gateway event mapping
- **WHEN** ChatOrchestrator emits ChatEvent
- **THEN** the system SHALL map ChatEvent to GatewayEvent for WebSocket transmission

#### Scenario: Gateway API compatibility
- **WHEN** a client sends a GatewayRequest via WebSocket
- **THEN** the system SHALL return GatewayEvent stream (unchanged external API)
