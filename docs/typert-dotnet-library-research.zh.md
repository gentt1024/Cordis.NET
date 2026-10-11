# 原生 Typert 成熟库复用调研，2026-10-10

[English](typert-dotnet-library-research.md)

本研究附录属于[原应用基础设施范围](development.zh.md#2026-10-09-应用基础设施范围续账)，为大规模修改之前的架构判断提供证据。它不引入实现决定、包选型、新规划或验收结论。[upstream.lock.json](../upstream.lock.json) 中的 DSH `639ed015397290b3745d163aafe02ffee4aa3f84` 继续作为行为依据。

官方文档与源码核对日期为 2026-10-10。下列仓库链接固定所检查的库版本，仅是调研快照，并非选定发布版本。未安装依赖，未编译或运行候选库。Cordis.NET 既有证据仍见[验证记录](validation.zh.md)。

## 复用判断

现有实现已经使用 Roslyn、System.Text.Json 与 ASP.NET Core。继续使用这些依赖是当前最有依据的选择。新增库可以减少 Schema、DTO 或 carrier 的工作，但没有一个提供 Cordis 的所有权合同。

| 候选 | 已核实的库能力 | 适用位置与语义边界 |
|---|---|---|
| Roslyn API 与增量生成器 | [编译器模型](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/compiler-api-model) 提供语法、符号与语义分析。[生成器 cookbook](https://github.com/dotnet/roslyn/blob/ad34e675fc51beef49d66cb8005af3752895727d/docs/features/incremental-generators.cookbook.md) 覆盖属性发现，并警告保留 Symbol 会保留旧 Compilation。 | 继续生成直接 C# 调用与编译器无关描述。Roslyn 提供分析机制，不提供 Typert 类型模型、发布合同或运行时 registry。完整源类型图仍需要项目自身建模。 |
| System.Text.Json | [源生成元数据](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation) 支持不依赖默认 Reflection 的序列化与反序列化。[JsonSchemaExporter](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/extract-schema) 接受 `JsonTypeInfo`，描述其 JSON 合同。 | 继续使用显式元数据。它描述序列化 DTO，不描述任意 C# 源类型或拥有生命周期的 Context 图。[可空性约束](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/nullable-annotations) 不覆盖根、集合元素和泛型成员；必填与可空也不同。自定义 converter 的语义需要显式合同。 |
| NJsonSchema | [README](https://github.com/RicoSuter/NJsonSchema/blob/18ba2ccfd20d795033d00b5e01585a94e2b78486/README.md) 说明 Schema 读取/校验、基于 Reflection 的 CLR Schema 生成及 C#/TypeScript DTO 生成。 | 可作为构建期 Schema 到 DTO 发射的候选。必须保留 JSON 字段名、可空性、tuple 位置、引用及对不支持关键字的拒绝。“Draft v4+”不能证明支持当前 exporter 的全部关键字。[C# 设置](https://github.com/RicoSuter/NJsonSchema/blob/18ba2ccfd20d795033d00b5e01585a94e2b78486/src/NJsonSchema.CodeGeneration.CSharp/CSharpGeneratorSettings.cs) 将 STJ 生成描述为实验性且不完整。 |
| NSwag | [工具链](https://github.com/RicoSuter/NSwag/blob/63daf8fcc3a25151b62eb4b326a1e8ea048a0d41/README.md) 读取/生成 OpenAPI，生成 .NET/TypeScript HTTP 客户端，使用 NJsonSchema 生成 DTO。 | 仅适用于明确选择的 OpenAPI 投影。HTTP operation 自身不能表达 Fiber 所有权、提供者撤销或 Context lookup。选择 STJ 模板不等于 AOT 安全，必须检查生成的序列化调用与元数据。不能为了使用生成器而把 Remote 改造成 REST。 |
| ASP.NET Core HTTP/NDJSON | [Response stream 与 pipeline](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/middleware/request-response?view=aspnetcore-10.0) 提供缓冲写入及显式 flush。[Native AOT 文档](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/native-aot?view=aspnetcore-10.0) 列出 Minimal API 的部分支持及源生成 JSON 要求。 | 继续现有 carrier。ASP.NET 提供 HTTP 机制；Cordis 继续负责 NDJSON envelope、流顺序/清理、授权与错误映射。流式 JSON 需要 metadata mode，不能只生成 serialization fast path。 |
| gRPC/gRPC-Web | [.NET gRPC 客户端与服务端支持 Native AOT](https://learn.microsoft.com/en-us/aspnet/core/grpc/native-aot?view=aspnetcore-10.0)。[gRPC-Web](https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb?view=aspnetcore-10.0) 支持浏览器 unary/server-streaming，但浏览器客户端不能使用 client/bidirectional streaming。 | 可作为明确适配合同的替代 carrier。Protobuf message、status error 与服务 descriptor 需要转换。gRPC-Web 不能关闭浏览器 Peer/uplink 范围。原生 .NET HTTP/2 流与浏览器能力应分开。 |
| StreamJsonRpc | [概述](https://github.com/microsoft/vs-streamjsonrpc/blob/cc024406511b9d17ffee22ee6eb59e74ff8bcc53/README.md) 提供 stream、WebSocket、pipe 上的 JSON-RPC、取消与代理。 | 是可信的桌面/daemon IPC 候选，不能直接替换 Typert peer。[部分 AOT 路径](https://github.com/microsoft/vs-streamjsonrpc/blob/cc024406511b9d17ffee22ee6eb59e74ff8bcc53/docfx/docs/nativeAOT.md) 要求生成代理/interceptor、target metadata 及适合的 formatter。STJ 还需要 serializer metadata，以及异步 enumerable/progress 的泛型注册。STJ marshalable object 不支持 AOT；Nerdbank MessagePack 有另一条支持路径与编码。浏览器/TypeScript 互操作需单独证明。 |

## 生成与运行边界

生成器或工具在构建期使用 Reflection，本身不妨碍发射的应用使用 Native AOT。运行期程序集加载与 `Reflection.Emit` 则被 [Native AOT 排除](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)。生成的 DTO 仍需要兼容的运行时序列化。

保留当前作者显式提供的 `JsonSerializerContext`。[普通源生成器看到相同的输入 Compilation](https://github.com/dotnet/roslyn/blob/ad34e675fc51beef49d66cb8005af3752895727d/docs/features/source-generators.md)，不能看到彼此的普通输出。新文档中的 pre-compilation 生成仅接受非 Compilation 输入，不能证明在固定 SDK 上从所发现的作者方法同轮生成元数据。自动 context 发射需要另行评估构建组织。

## 继续由 Cordis 拥有的语义

所评估的库均不能替代 Fiber 所有的原子注册、精确 owner 撤销、保留的 definition history、当前 Service 权威、Context 选择/对象 lookup 或 definition/provider 代际检查。这些职责来自固定 [registry](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/typert/registry/src/service.ts)、[Gateway](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts) 及明确的[原生适配](compatibility.zh.md#原生-typert-合同与传输)。复用任何 carrier 时仍需保留它们。

[HttpClient 取消](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-10.0) 可以取消客户端 HTTP 操作，导致收不到响应；它不能重新定义 Host 调用语义或强制结束业务。固定 [unary 调用](https://github.com/deepseek-ai/deepseek-harness/blob/639ed015397290b3745d163aafe02ffee4aa3f84/packages/api/gateway/src/index.ts#L361) 在 abort 后仍保留成功业务结果，已调用方法在 abort 下失败才映射成取消。取消、撤销与代际有效性仍是不同职责。

Collectible ALC 卸载要求释放[对插件类型/实例的外部强引用](https://learn.microsoft.com/en-us/dotnet/standard/assembly/unloadability)。推断：host 持有的类型化客户端、DTO 元数据、delegate、proxy target 或 serializer resolver chain 可能保留提供者代际。应生成调用方 DTO，或使用经过审查的 host 共享合同程序集，避免引用可卸载的提供者实现。代际局部元数据应随 owner 退休。这些是设计候选，不是已实现的卸载保证。

## 依赖成本与待核查项

| 依赖 | 许可与维护影响 |
|---|---|
| 现有 Roslyn/STJ/ASP.NET | MIT：[Roslyn](https://github.com/dotnet/roslyn/blob/ad34e675fc51beef49d66cb8005af3752895727d/License.txt)、[runtime](https://github.com/dotnet/runtime/blob/v10.0.0/LICENSE.TXT)、[ASP.NET](https://github.com/dotnet/aspnetcore/blob/v10.0.0/LICENSE.txt)。继续核对 SDK/runtime 兼容性。 |
| NJsonSchema/NSwag | [MIT](https://github.com/RicoSuter/NJsonSchema/blob/18ba2ccfd20d795033d00b5e01585a94e2b78486/LICENSE.md)、[MIT](https://github.com/RicoSuter/NSwag/blob/63daf8fcc3a25151b62eb4b326a1e8ea048a0d41/LICENSE.md)。NJsonSchema 增加 [Namotion.Reflection/Newtonsoft.Json](https://github.com/RicoSuter/NJsonSchema/blob/18ba2ccfd20d795033d00b5e01585a94e2b78486/src/NJsonSchema/NJsonSchema.csproj)；生成模板变化需要合同审查。 |
| gRPC .NET | [Apache-2.0](https://github.com/grpc/grpc-dotnet/blob/7db6c142a2dd5e0fefb0681e67766e339a15dc47/LICENSE)。增加 protobuf/tooling 及 carrier 转换。 |
| StreamJsonRpc | [MIT](https://github.com/microsoft/vs-streamjsonrpc/blob/cc024406511b9d17ffee22ee6eb59e74ff8bcc53/LICENSE)。其[依赖集](https://github.com/microsoft/vs-streamjsonrpc/blob/cc024406511b9d17ffee22ee6eb59e74ff8bcc53/src/StreamJsonRpc/StreamJsonRpc.csproj) 包含 threading、stream 与多种 serializer 包。Formatter/proxy 选择需要审查。 |

WPF、Avalonia、Godot 与 Blazor 是目标环境，不是已经验证的 Cordis.NET host。仓库目标为 `net10.0`。[Godot stable 文档](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/c_sharp_basics.html) 描述不同的 SDK 要求，并排除 Godot 4 C# Web export；桌面 SDK 最低要求不能证明库 TFM/export 兼容性。Blazor 浏览器限制与 Native AOT executable 支持也是不同的部署问题。

未验证项包括 Schema dialect 往返、生成客户端 AOT 发布、collectible 代际引用留存、浏览器互操作及完整取消/流适配。未来选定依赖时保留 notices 与 licenses。本调研不作版本、工期或性能承诺。
