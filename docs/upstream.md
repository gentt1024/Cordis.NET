# Upstream baseline and provenance

[中文](upstream.zh.md)

## Locked baseline

| Role | Repository | Revision or version |
|---|---|---|
| Behavior target | `deepseek-ai/deepseek-harness` | `ddefc45fbc7f8e46dd73185e68295696d1297887` |
| Harness release label | DeepSeek Harness | `0.1.6-alpha.2` |
| Vendored package | `@deepseek-ai/cordis` | `4.0.2` |
| Core and loader origin | `cordiverse/cordis` | `56b3d4f725681cf4556c1a8695a709cc3b6eed74` |
| Extension origin recorded upstream | `deepseek-harness/cordis` | `abb0a307cb1d3b0947f455d590cf5ba922d4caa4` |

`upstream.lock.json` is authoritative. The implementation uses the DSH vendored source when it differs from an origin repository. Code behavior, source tests, and upstream documentation are separate evidence categories. The archived vendor README is retained as a [historical snapshot](reference/dsh-vendor-readme.snapshot.md), including its original local links.

## Updating the baseline

Do not float an upstream branch. Pin a new commit, inventory its vendored source and tests, review license changes, regenerate maps, classify every added or removed assertion, run differential JIT and static AOT scenarios, and validate packages on Windows and Linux. Record intentional adaptations instead of silently changing their disposition.
