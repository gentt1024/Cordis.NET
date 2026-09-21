# Security policy

## Supported versions

Cordis.NET is currently an alpha preview. Security fixes are made on the current development line; no released version has a long-term support commitment yet.

## Reporting a vulnerability

Do not disclose an unpatched vulnerability in a public issue. Use [GitHub private vulnerability reporting](https://github.com/gentt1024/Cordis.NET/security/advisories/new). Include the affected component and version, a minimal reproduction, impact, and any known mitigation.

## Trust model

Cordis plugins and configuration execute with the privileges of the hosting process. Context and fiber boundaries organize ownership and lifecycle; they are not security isolation.

`Cordis.JavaScript` evaluates `!!js` through Jint. A timeout is not a sandbox or tenant boundary. Evaluate only trusted configuration. CLR modules also execute with process privileges. Applications that need isolation must use suitable operating-system process, identity, filesystem, and network boundaries.
