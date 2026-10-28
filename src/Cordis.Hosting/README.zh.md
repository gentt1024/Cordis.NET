# Cordis.NET.Hosting

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Hosting/README.md) | 中文

将一个 Cordis 根 Context 接入 Microsoft Generic Host 的启动与退出，将选定 DI 服务提供给 Cordis，同时保留容器的资源所有权。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.Hosting --version 0.2.1-alpha.1
```

## 按任务使用

在已有 Generic Host 的应用中调用 `services.AddCordis(...)`。通过 `options.Borrow<MyService>("service")` 引入已注册服务，用 `options.Configure((ctx, services, cancellation) => ...)` 执行异步启动；回调位于 Cordis 执行域中。见[Generic Host 接线（英文）](https://github.com/gentt1024/Cordis.NET/blob/main/docs/usage.md)及[公共选项与实现](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Hosting/CordisHostExtensions.cs)。

独立应用可直接使用 Core 的根 Context，Hosting 是可选包。

## 生命周期与支持范围

Generic Host 协调根 Context 的启动与退出，启动失败会释放该 Context。借用服务在退出时撤销，由原容器负责释放。这个静态适配器支持 Native AOT；动态插件与 serializer 选择仍有各自限制。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
