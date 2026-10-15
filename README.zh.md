# Cordis.NET

[English](README.md)

Cordis.NET 是固定 DSH 所维护 Cordis 运行时及通用应用基础设施的忠实、地道 .NET port。可以编写普通 C# 插件和服务、组合应用，或向 .NET／Web 客户端开放选定服务。Remote 是可选边界；普通插件开发不需要 Node。

固定上游行为和有依据的平台适配决定实现。历史试验不构成另一套框架规范或兼容义务，具体见[兼容性说明](docs/compatibility.zh.md)。

Cordis.NET 独立维护，不是 DeepSeek、Cordiverse 或 Microsoft 的官方项目。行为基线为 deepseek-ai/deepseek-harness 的 639ed015397290b3745d163aafe02ffee4aa3f84，其中 vendored Cordis 版本为 4.0.4；任意 TypeScript 插件不是支持的部署格式。

## 从任务开始

| 任务 | 所需包与入口 |
|---|---|
| 运行普通 .NET 插件和服务 | Core；需要 Loader、Profile 和配置时添加 Composition。[仅使用包的首个应用](docs/getting-started.zh.md)。 |
| 向 .NET 或 Web 消费者开放 Host | Composition 提供声明提取、生成绑定、独立客户端和共享 Remote 合同；AspNetCore 提供带授权的 HTTP／WebSocket 宿主适配。[作者指南](docs/authoring.zh.md)。 |
| 安装并运行独立 CLR 插件 | Clr 在现有配置操作之上提供 SDK 工具链和稳定制品加载。[制品流程](docs/clr-artifacts.zh.md)、[托管插件](examples/ManagedPlugin/README.md)、[托管应用](examples/ManagedApplication/README.md)。 |

可选 Hosting 接入 Microsoft Generic Host；Extensions 提供选定运行时扩展；JavaScript 通过 Jint 求值受信任配置表达式，不提供 Node 环境或沙箱。静态 Core／Composition 应用支持 Native AOT；动态 CLR 加载和 Jint 要求普通运行时。CLI 包为 Cordis.NET.Tool。包 ID 使用 Cordis.NET.*，命名空间和程序集使用 Cordis.*。

## 选择包批次

当前源码选择 `0.2.0-alpha.5` 进行发行验证，尚未公开发布到 NuGet。取得对应验收批次后，从匹配的 feed 消费这一精确版本。已发布 alpha.4 不包含这些修改。[当前支持与发行状态](docs/development.zh.md#当前支持与发行状态)区分已实现能力、已知限制和待决事项；[验证索引](docs/validation.zh.md#当前候选索引)标明实际验过的包。

按[入门指南](docs/getting-started.zh.md)安装选定批次，无需 clone 即可运行。若要从源码探索相同的组合流程，使用 global.json 指定 SDK：

```console
dotnet run --project examples/Composition/Composition.csproj
```

该示例挂载静态插件、读取服务并更新配置。[托管应用](examples/ManagedApplication/README.md)进一步演示包安装和浏览器模块；仅可选前端构建需要 Node。

## 文档

- [入门](docs/getting-started.zh.md)：包选择、首次调用与关闭。
- [作者指南](docs/authoring.zh.md)：声明、生成消费者、错误和所有权。
- [核心概念](docs/core-concepts.zh.md)与[架构](docs/architecture.md)：运行时和子系统职责。
- [开发](docs/development.zh.md)：当前支持、维护导航与验证。
- [兼容性](docs/compatibility.zh.md)、[上游来源](docs/upstream.zh.md)与[升级矩阵](docs/upgrade-0.2.0-rc.2.zh.md)：固定行为和平台适配。
- [发行准备](docs/publication-readiness.zh.md)、[变更记录](CHANGELOG.md)、[贡献指南](CONTRIBUTING.zh.md)、[安全](SECURITY.md)和[归属声明](NOTICE.md)。

目前为早期预览。稳定发行前公共 API 和适配仍可调整；持久数据、Git 历史和已发布包身份继续保全。
