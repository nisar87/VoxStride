# ADR-0003: Native C# core instead of adopting UFO²/UFO³

- Status: accepted (provisional pending VM benchmark)
- Decision: Do not take UFO as a dependency. Use it as an architectural benchmark only.
- Rationale: UFO is a Python research stack with a broad action surface. VoxStride requires a strict local authorization boundary, typed tools, a journal and verification, a small installer and no mandatory Python. The native core already implements these.
- Follow-up: P0 benchmark of UFO in a disposable VM (setup time, p50/p95, licensing). Not run on the owner's PC.
