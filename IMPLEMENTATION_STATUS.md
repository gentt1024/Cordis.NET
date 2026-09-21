# Implementation status

Cordis.NET `0.1.0-alpha.2` implements the pinned runtime and composition scope as an early preview. The public status is maintained in [compatibility](docs/compatibility.md), [validation](docs/validation.md), and the machine-readable maps under `docs/`.

The current publication candidate passes the complete Windows gate with 456 tests, the pinned source suites, JIT/AOT differential checks, and isolated package consumers. Package XML documentation, Source Link metadata, symbol packages, and release artifact hashing are enabled. Hosted Windows/Linux validation is the next public gate; this publication work does not expand the runtime compatibility claim.

The remaining external steps are tracked in [publication readiness](docs/publication-readiness.md).
