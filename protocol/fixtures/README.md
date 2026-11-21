# Protocol fixtures

`valid/` must parse and validate in every implementation (Rust agent, Swift host,
C# host) and against `../v1.schema.json`. `invalid/` must be rejected by all of them,
either at parse time or by the shared validation rules (version, score range, list sizes).

Add a fixture here before changing a message, so every side's tests pick it up.
