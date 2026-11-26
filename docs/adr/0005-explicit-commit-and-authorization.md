# ADR-0005: Explicit commit, run authorization and manifest-bound approvals

- Status: accepted
- Decision: Only `ConversationCoordinator` creates `CommandEnvelope`s, and only on explicit finish. `RunAuthority` mints an unguessable, generation-bound, expiring authorization tied to the transcript hash. `ToolBroker` enforces a fixed check order. Moderate operations need an `Approval` bound to the exact manifest digest, created only by a click in `ApprovalWindow`. Standing rules may pre-approve only reversible same-root moves for one folder.
- Consequences: models cannot authorize, and voice cannot approve risky steps. Every effect is journaled before and after.
