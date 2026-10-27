# Upstream baseline and provenance

[中文](upstream.zh.md)

The locked upstream commit is distinct from the locally assembled verification commit. `harness.sourceAdaptation` selects the [reviewed patch record](../reference/candidate-adaptation.json), which binds the upstream tree, one changed implementation file, patch digest and reproducible derived commit. Original upstream tests are unchanged. The filename retains its S10 discovery origin; the formal lock now selects this record. See [compatibility](compatibility.md) for native adaptations and [capability coverage](candidate-coverage.md) for the bounded scope.

## Locked baseline

| Role | Repository | Revision or version |
|---|---|---|
| Behavior target | `deepseek-ai/deepseek-harness` | `687f282e8625c6b8d11ff58211a90524be2e4317` |
| Harness release label | DeepSeek Harness | `0.2.1-rc.2` |
| Vendored package | `@deepseek-ai/cordis` | `4.0.4` |
| Core and loader origin | `cordiverse/cordis` | `56b3d4f725681cf4556c1a8695a709cc3b6eed74` |
| Extension origin recorded upstream | `deepseek-harness/cordis` | `abb0a307cb1d3b0947f455d590cf5ba922d4caa4` |

`upstream.lock.json` is authoritative. The implementation uses the DSH vendored source when it differs from an origin repository. Code behavior, source tests, and upstream documentation are separate evidence categories. The archived vendor README is retained as a [historical snapshot](reference/dsh-vendor-readme.snapshot.md), including its original local links.

## Updating the baseline

Do not float an upstream branch. Pin the target, inspect changes affecting the promised native capabilities and their necessary dependencies, add tests that expose each behavior difference, and record platform adaptations. Preserve immutable historical inventories; the whole DSH path inventory is a discovery aid, not an upgrade prerequisite. Run the native verification, relevant source comparisons, JIT/static AOT traces and isolated package consumers on Windows and actual Linux before promoting a baseline.
