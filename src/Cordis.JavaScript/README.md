# Cordis.NET.JavaScript

English | [中文](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.JavaScript/README.zh.md)

Evaluate trusted `!!js` configuration expressions through Jint with access to the live Cordis service view. Use it only when configuration needs JavaScript expressions.

## Install

Use the .NET 10 SDK and matching Cordis.NET package versions.

```console
dotnet add package Cordis.NET.JavaScript --version 0.2.1-alpha.1
```

## Use this package

Pass `new JintExpressionEvaluator()` to Loader, ApplicationBoot or ProfileSession. Without an evaluator, raw `!!js` expressions fail explicitly. See [configuration expression setup](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md).

The evaluator supplies CLR views and functions through the current Context. It provides no Node modules or Node globals, and evaluation does not require a Node installation.

## Lifetime and support

Use an ordinary .NET runtime; Native AOT is not supported for this adapter. Each evaluation creates a fresh Jint engine. `Timeout` and `MaxStatements` bound evaluation work but do not make untrusted code safe. Loaded expressions can access supplied host capabilities, so execute only trusted configuration.

Cordis.NET is independently maintained and is not an official project of DeepSeek, Cordiverse or Microsoft.
