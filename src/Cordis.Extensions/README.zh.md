# Cordis.NET.Extensions

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Extensions/README.md) | 中文

通过 ProfileSession 运行应用的配置 Profile，协调刷新与 HMR，并提供定时器和控制台服务。本包基于 Core 与 Composition。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.Extensions --version 0.2.1-alpha.1
```

## 按任务使用

使用 `ProfileSession.StartAsync` 管理 Context、Loader、配置 include 和可选监听器。编辑及包操作使用 `session.ConfigurationOperations`，让它们共用会话队列。参考[组合与部署（英文）](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md)或完整的[托管应用示例](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md)。

关闭监听后仍可调用 `RefreshAsync` 手动刷新，检查返回的 `ProfileReloadResult`，包括 `RequiresRestart` 与警告。[应用作者指南](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.zh.md#启动与配置应用)说明启动策略、诊断和配置准入。

## 生命周期与支持范围

释放会话会停止它拥有的运行时与监听器。HMR 协调更新，不提供业务事务，也不强制终止任意业务工作。静态应用可使用 Native AOT；动态 CLR 加载与可选 `!!js` 表达式分别需要普通运行时适配器。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
