# Decisions
- Add new interface ISessionLifecycle with methods:
- BeginSessionAsync(title, agentId) -> SessionData
- EndSessionAsync(sessionId) -> Task
- CloneSessionAsync(sourceId, newTitle) -> SessionData
- Tests designed with Moq to validate the contract without concrete implementation.
- No backward-compatibility changes to ISessionManager; this is an independent lifecycle extension.
