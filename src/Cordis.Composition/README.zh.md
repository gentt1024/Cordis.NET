# Cordis.NET.Composition

[English](https://github.com/gentt1024/Cordis.NET/blob/main/src/Cordis.Composition/README.md) | 中文

通过 Loader、Profile 和协调编辑组合配置式应用，也可声明选定的 Remote 服务并生成 .NET 与 Web 消费者。包内包含 Typert analyzer、compiler 和 MSBuild targets。

## 安装

使用 .NET 10 SDK，并保持 Cordis.NET 包版本一致。

```console
dotnet add package Cordis.NET.Composition --version 0.2.1-alpha.1
```

## 按任务使用

- 加载静态注册模块并更新配置：[Composition 示例](https://github.com/gentt1024/Cordis.NET/blob/main/examples/Composition/Program.cs)。
- 通过现有 Profile 操作提供实时配置表单：[Settings 指南](https://github.com/gentt1024/Cordis.NET/blob/main/docs/settings.zh.md)。
- 声明 Remote Service、注册贡献并连接调用方：[Remote 作者指南](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.zh.md#声明与生成原生-remote-合同)。
- 提取声明或生成独立 DTO 与客户端：[模型与客户端流程](https://github.com/gentt1024/Cordis.NET/blob/main/docs/authoring.zh.md#生成模型与独立客户端)。

C# 生成使用选定的 .NET SDK 与显式 STJ metadata，不需要 Node；浏览器作者编译使用单独的 Node 工具。

## 生命周期与支持范围

普通插件调用留在进程内。Remote 客户端、Registry、Gateway 和 Connection 复用应用 Context，Host 管理授权与连接生命周期。静态组合可用于 Native AOT，但所选 serializer 与后端形状须受支持，见[后端限制](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.zh.md#生成后端支持范围)。

已发布的 alpha.1 不包含 main 后续的增量生成与跨程序集 Host 默认值修正；选择工具前核对[发行与源码状态](https://github.com/gentt1024/Cordis.NET/blob/main/docs/development.zh.md#当前支持与发行状态)。Settings 使用数值表单 revision，完整配置哈希另用于源码一致性检查。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。
