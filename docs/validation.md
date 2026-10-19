# Validation

[中文](validation.zh.md)

## Choose the check

Run `python scripts/check-docs.py` for documentation and owning tests for code changes. Runtime, package, compatibility and release changes require `python scripts/verify.py`. See the [maintenance guide](development.md) for commands. Keep required CI checks.

## Released version evidence

The [alpha.5.2 release](https://github.com/gentt1024/Cordis.NET/releases/tag/v0.2.0-alpha.5.2) supplies the formal manifest, evidence and SHA256SUMS. Its tag identifies `1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4`; all seven jobs in the [formal run](https://github.com/gentt1024/Cordis.NET/actions/runs/39145687915) passed. Eight product packages and matching symbols came from the Linux validation batch; Example was excluded. Public NuGet payloads matched sealed bytes apart from the added NuGet signature. Public release attachments were downloaded and their digests checked.

## Accepted risks and evidence boundaries

Cold Jint construction and native initialization can delay HMR refresh, queued work and draining shutdown. Historical timeouts remain failures. Sampling did not explain the entire delay or establish a latency bound. The ten-second regression remains unchanged; new failures still fail the gate. Missing progress, error publication or cleanup, or unacceptable real cold-start latency, requires concrete follow-up.

Windows/Ubuntu have full package and AOT validation. macOS has same-batch authoring/browser consumption only, not complete runtime, application or AOT acceptance. Rider acceptance does not cover other IDEs. Windows document opening proves system handoff, not editor contents or other desktop platforms. See the [maintenance guide](development.md) for backend restrictions and outstanding named UI integration evidence. Author-directory protection does not promise a hostile-filesystem sandbox.

## Historical evidence

The [original validation record](https://github.com/gentt1024/Cordis.NET/blob/1ed99e39c44c8f7ecbd064b365b8ebb31530f3c4/docs/validation.md) retains each candidate identity, failure and then-pending work. [verification](../verification) retains tracked receipts and machine-consumed inputs. Git does not preserve untracked packages, TRX files, screenshots or expired CI artifacts; a hash does not prove that the original bytes remain available.

Record PR merge checkouts separately from branch heads. Local supplements do not turn a failed complete gate into a passing run; different candidates cannot be combined into one acceptance result.
