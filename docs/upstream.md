# Upstream baseline and provenance

[中文](upstream.zh.md)

## Locked baseline

| Role | Repository | Revision or version |
|---|---|---|
| Behavior target | `deepseek-ai/deepseek-harness` | `639ed015397290b3745d163aafe02ffee4aa3f84` |
| Harness release label | DeepSeek Harness | `0.2.0-rc.2` |
| Vendored package | `@deepseek-ai/cordis` | `4.0.4` |
| Core and loader origin | `cordiverse/cordis` | `56b3d4f725681cf4556c1a8695a709cc3b6eed74` |
| Extension origin recorded upstream | `deepseek-harness/cordis` | `abb0a307cb1d3b0947f455d590cf5ba922d4caa4` |

`upstream.lock.json` is authoritative. The implementation uses the DSH vendored source when it differs from an origin repository. Code behavior, source tests, and upstream documentation are separate evidence categories. The archived vendor README is retained as a [historical snapshot](reference/dsh-vendor-readme.snapshot.md), including its original local links.

## Updating the baseline

Do not float an upstream branch. Pin the target, inspect changes affecting the promised native capabilities and their necessary dependencies, add tests that expose each behavior difference, and record platform adaptations. Preserve immutable historical inventories; the whole DSH path inventory is a discovery aid, not an upgrade prerequisite. Run the native verification, relevant source comparisons, JIT/static AOT traces and isolated package consumers on Windows and actual Linux before promoting a baseline.
