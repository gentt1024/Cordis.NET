# Cordis.NET.Core

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Core/README.md) | 中文

构建进程内插件应用，使用服务、依赖驱动的激活、事件和插件实例所属的清理。Core 不需要 Node、网络服务器或 Remote。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.Core --version 0.2.1-alpha.1
```

## 按任务使用

从[第一个插件](https://github.com/gentt1024/Cordis.NET/blob/main/docs/getting-started.zh.md)开始。完整控制台程序注册插件、读取服务、输出 `41`，然后释放插件。

应用代码通过 `Context.RunAsync` 进入 Cordis。`Plugin<T>` 定义插件，返回的 `Fiber` 表示其注册实例。[核心概念](https://github.com/gentt1024/Cordis.NET/blob/main/docs/core-concepts.zh.md)说明服务视图、依赖与 effect。需要由配置选择模块和实例时，再添加 Composition。

## 生命周期与支持范围

Fiber 拥有自己的注册与已登记的清理。撤销服务不会释放被注册对象。`WaitAsync` 等待当前生命周期工作，缺少依赖时 Fiber 仍可能处于 pending。静态应用支持 Native AOT；运行时 DLL 加载由 Clr 提供，需要普通运行时。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
