# ADR-0002: Provider adapters over raw HTTPS/SSE

- Status: accepted
- Decision: Implement Claude (Messages), OpenAI (Responses) and Gemini (streamGenerateContent) with typed `HttpClient` plus a shared SSE reader instead of vendor SDKs.
- Rationale: identical event semantics (`ToolCallCompleted` only after a successful terminal event), no SDK auto-execution of tools, no forced dependency churn, testable with recorded-shape fixtures, explicit preservation of continuation data (Claude content blocks, OpenAI output items incl. encrypted reasoning with `store:false`, Gemini parts with thought signatures).
- Consequences: we must track API changes ourselves. Contract tests pin the shapes, and live smoke tests are opt-in with owner keys.
