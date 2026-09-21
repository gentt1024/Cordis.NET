# Updating the locked behavior target

1. Check out a candidate DeepSeek Harness commit separately. Preserve all existing checkouts.
2. Compare the full `vendor/README.md`, package manifests, each vendored source tree, boot HMR,
   app-boot, plugin-manager composition behavior and package-manifest types. Review every local
   modification, including new entries at the end of the log.
3. Find the actual source-origin commits and original tests; do not substitute npm latest.
4. Run `node reference/inventory.mjs <dsh> <cordis-origin>` on the locked candidates. Review
   removed/added/parameterized tests and reconcile stable IDs with port mappings. A source
   probe is not an original-test adaptation. Keep gaps explicit.
5. Update the reference loader's commit assertion and source excerpts only with a reviewed
   baseline update. Reference code must still execute the original algorithms. Re-run ordered
   differential traces before modifying C# expectations.
6. Change C# behavior and regressions, update the public API snapshot only when intentional,
   and execute JIT, native AOT, CLR replacement and isolated NuGet consumption.
7. Commit lock, source, mappings and evidence together. Record platform limits. Generate the
   source ZIP with `python scripts/source-archive.py`; it requires a clean committed tree and
   validates extracted paths, project references and every tracked file's SHA-256.
