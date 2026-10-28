# Cordis.NET.JavaScript

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.JavaScript/README.md) | 中文

通过 Jint 求值可信 `!!js` 配置表达式，并访问当前 Cordis 服务视图。配置需要 JavaScript 表达式时再添加本包。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.JavaScript --version 0.2.1-alpha.1
```

## 按任务使用

向 Loader、ApplicationBoot 或 ProfileSession 传入 `new JintExpressionEvaluator()`。没有 evaluator 时，原始 `!!js` 表达式会明确失败。接线见[配置表达式说明（英文）](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md)。

求值器通过当前 Context 提供 CLR 视图与函数，不提供 Node 模块或 Node 全局对象，求值不需要安装 Node。

## 生命周期与支持范围

使用普通 .NET 运行时，本适配器不支持 Native AOT。每次求值创建新的 Jint engine。`Timeout` 与 `MaxStatements` 限制求值工作，但不保证不可信代码安全。表达式可以访问提供的 Host 能力，因此只执行可信配置。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
