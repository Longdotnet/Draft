# AI failover

All text/chat AI paths owned by `AiAssistantService` must route through `IZaloAiGateway` rather than sending provider HTTP requests directly.

This includes legacy intent classification, member-activity classification, reminder/share-slot/slot-transfer/team-preference extraction, factual rewriting, and general chat. The gateway owns provider ordering, model selection, credentials, timeout, retry, failure classification, and failover.

`AiAssistantService` may keep legacy payload construction and response validation for behavioral compatibility, but its transport must not depend on only `Ai:Endpoint`, `Ai:ApiKey`, and `Ai:Model`. A configured `Ai:Providers` pool is sufficient.

Provider failures must not perform domain mutations. AI remains interpretation/phrasing only; deterministic backend state remains authoritative.
