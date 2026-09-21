# Contributor agent guidance

- Treat `upstream.lock.json` as the behavior-baseline authority; do not use floating revisions.
- Read `docs/compatibility.md` before changing behavior and record adaptations explicitly.
- Keep English and Chinese paired documents synchronized.
- Run focused tests; run `python scripts/verify.py` for runtime, package, compatibility, or release changes.
- Do not commit credentials, personal paths, machine-specific raw logs, build caches, or private handoff material.
- Preserve third-party notices and licenses. Discuss public API or intentional semantic changes first.
