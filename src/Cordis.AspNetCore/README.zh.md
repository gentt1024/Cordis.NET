# Cordis.NET.AspNetCore

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.AspNetCore/README.md) | 中文

通过有授权的 HTTP unary 与 WebSocket mux 路由提供选定 Cordis 服务，并在运行中的 ProfileSession 上挂载管理能力，复用其配置与生命周期所有者。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.AspNetCore --version 0.2.1-alpha.1
```

## 按任务使用

使用 .NET 10 ASP.NET Core Host。

1. 声明 Service，在现有 Cordis Context 中提供实际实例。
2. 将生成贡献注册到 `TypertRegistry`，在该执行域创建 Host 所有的 `TypertHostConnection`。
3. 在映射 `MapCordisRemote` 前添加 `UseWebSockets()`，并提供 Host 授权回调。
4. 生成客户端连接到实际映射的 Remote 地址。

集成代码见[Host 与客户端接线](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.zh.md#登记调用与撤销合同)。Profile 管理由 `CordisManagement.Map` 提供 Host 状态、原始配置和客户端制品，`MountPluginManagerAsync` 在同一操作所有者上挂载生成式管理。[ManagedApplication](https://github.com/gentt1024/Cordis.NET/blob/main/examples/ManagedApplication/README.md) 提供完整应用。

脱敏实时表单见[Settings 指南](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.zh.md)。通过显式 `JsonTypeInfo` 合同暴露选定进程内服务时，使用 `MapCordisService`。

## 生命周期与支持范围

Host 提供认证、授权和请求策略，本适配器不提供账号系统。构建权限与安装权限分开；完整配置使用 `configuration-read` 和 `configuration-write`，因为原始值与默认值可能包含秘密。

管理修改要求 `/state` 返回的 generation，读取也可发送 `If-Cordis-Generation`。重连先订阅，再读取当前状态和 graph；通知不承诺持久重放。先关闭客户端与传输，再释放它们借用的 Context 和服务。所选 AOT 路线要求显式 serializer metadata，动态 CLR 加载仍需要普通运行时。

构建授权允许一次准备，包括从选定源解析的依赖目标。检查所得根归档摘要将该根包绑定到准备操作，不批准每个传递依赖摘要。单独的 `build` 权限回调可检查来源和根摘要。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
