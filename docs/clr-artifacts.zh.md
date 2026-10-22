# CLR 制品与运行实例

[English](clr-artifacts.md)

普通 CLR 启动直接使用稳定制品。新进程或新的 collectible `AssemblyLoadContext` 不再创建另一份 bundle。托管依赖隔离、显式共享契约及既有实现替换协议保持不变。原生库遵循 CLR 和操作系统规则；独立 ALC 不承诺独立 native 静态状态。

## 标准包工作流

使用 `DotnetPluginToolchain` 与 `ProfileSession.ConfigurationOperations`。工具链负责获取、SDK 准备、完整文件校验记录、发布及制品保留。应用提供源选择、构建授权与通常的插件生命周期，无需实现接纳服务。

1. 构建正常插件 NuGet 包，包含 `cordis.plugin.json`、私有依赖、资源和 Worker 文件。[托管插件示例](../examples/ManagedPlugin/README.md)说明元数据。运行数据放在代码目录之外。
2. 使用 `new ClrModuleResolver(sharedContracts: [...])` 与 `DotnetPluginToolchain(profileDirectory, resolver, sources)`。如[托管应用示例](../examples/ManagedApplication/README.md)，将 `toolchain.Bundles` 传给 `ProfileLaunch.LocalBundles`。
3. 以明确版本和构建授权调用 `InstallPackageAsync`。准备阶段一次性发布到独立 `.cordis/work` 输出；为声明的插件按其 DLL basename 提供 SDK 部署依赖清单，供 `AssemblyDependencyResolver` 使用。产品包装器可在返回准备结果之前补齐自己的元数据。
4. 发布将同一份完整输出移动到 `.cordis/packages/<name>/<version>`，在旁置 `.<version>.files.json` 中记录完整文件集合与 SHA256，再校验并注册。这份记录属于工具链元数据，位于已批准制品树之外，产品清单不需要排除项。既有版本目录和记录都不覆盖；Profile 准入与完整准备/发布文件树比较继续使用原安装流水线。
5. 启动先验证安装制品的清单和身份，再注册代码。装载直接使用这个目录；后续 Resolve 复用 lease。新进程重新验证，不重新复制。缺失记录、不完整或被修改的部署明确失败，不会重写清单来接受变化。
6. 升级已安装的 profile 自有包会准备并持久化新版本，然后报告 `restart-required`；本进程保留旧代码路由。Watcher 处理选择变化后可以在重启前启用或停用旧实现，普通配置编辑仍作用于旧实现。重启读取保存的版本。同版本重复请求和 installation-owned 替换仍拒绝。新版本启动失败不意味着自动回退旧版本。
7. `RemovePackageAsync` 取消选择、等待所属 fibers 结束，并解除 profile 依赖与 resolver 引用。CLR 文件仍保留给旧引用、延迟依赖、Worker 和其他进程。`PackageChange.Residuals` 返回保留版本目录，包括仍运行的旧版本和等待重启的新版本；逻辑移除不等于物理删除。工具链包装器应转发 `GetRetainedDirectories`，以保留完整报告。同一管理会话拒绝重新启用已移除身份，直到安装再次注册该身份；仅保留文件不构成启用资格。
8. 所有宿主、Worker 和其他消费者停止后，调用 `DotnetPluginToolchain.DeleteRetainedArtifactAsync(profileDirectory, name, version)`，或 `cordis delete-retained <profile> <name> <version>`。Profile 仍引用该精确版本时拒绝；CLI 还会拒绝正在运行的标准 CLI 宿主。删除 API 在检查引用和删除文件期间持有既有 `package.json.cordis-lock` 写锁，遇到配合该协议的写入者占锁时立即拒绝。Manifest 无法读取或格式无效时保留制品。部署方仍负责排除任意外部消费者及不遵守该锁协议的写入者。失败报告剩余路径；不自动扫描，不根据 GC 删除。

S4 候选工作流支持在同一 session 内先移除再安装。移除先通过 `GetLoadedModulesAsync` 检查已加载模块的所有权，包装器必须转发此查询。直接或外部 Fiber 会在改变选择前返回 `restart-required`，且 `RestartReason = "unmanaged-plugin"`。成功移除解除路由而不删除文件；解除后持久化失败，可以重试移除以完成已保存依赖的变更。另一个版本的重装加载新依赖闭包；同版本复用要求 receipt 有效且准备文件完整一致。有效但不同的文件树返回 `package-moved`，receipt 损坏仍失败。新准备对生成的 scaffold 使用 `DebugType=none`，比较不忽略任何文件。不要通过修改保留制品强行匹配。见[候选兼容性记录](compatibility.zh.md)。

信任边界是合作式部署，不是防御同权限代码的沙箱。校验记录在验证点发现内容变化；库自身之后不会覆盖或自动删除已发布制品。记录不替代发布者认证，也不阻止恶意并发写入。

旧工具链未保存文件记录的部署需要通过此工作流重新安装，例如先装到新 profile 或新精确版本再切换部署。启动不自动采信旧文件。低层外部目录 API 仍供宿主自有部署使用，其所有者提供完整稳定的文件。

## 开发与实现 HMR

使用作者正常项目和 targets 为新代码修订发布一个完整的新输出目录，旧输出在使用期间保持不变。注册 `ClrModuleDefinition`，通过 `ReplaceAsync` 与既有 Loader 替换回调切换。[CLR Probes 示例](../examples/Probes.Clr/Program.cs)使用独立 V1/V2 目录，不产生运行期副本。

`ClrModuleDefinition.LoadMode` 默认 `StableDirectory`。若外部开发目录确实需要原地重建，可以显式选择 `ShadowCopy`。准备之前须完成构建并停止整个目录的写入；此模式每次准备完整复制 bundle，不保证并发构建时获得一致快照。来源与副本目录不得重叠。复制范围包含私有依赖、原生依赖、Worker 与资源，不仅是入口 DLL。

`ClrUnloadObservation.LoadDirectory` 表示退休实例实际使用的目录。稳定装载时 `ShadowDirectory` 为 null，`TryDeleteShadow` 从不删除稳定来源。显式开发副本仅在所有文件消费者，包括 Worker，结束后才能调用删除。托管 wrapper 回收是附加观察，不证明此前提成立；native 占用仍可能推迟删除。

业务退休、候选激活、卸载请求、GC 和物理删除仍是独立结果。生产不强制 GC；此路线不提供保留制品的原地更新或通用 native 隔离能力。

## 检查已经准备的替换候选

应用需要在撤回任何旧合同前检查实际候选时，传入 `admitCandidate`；没有准入政策的宿主可以省略。注册名包含别名和未激活出口；没有 `IClrTypertModule` 的入口由 `GetContribution` 返回 null。别名共享模块、插件和缓存贡献。普通 resolver 读取在提交前仍看到已发布代际。

```csharp
await resolver.ReplaceAsync(
    replacements,
    switchFibers: async pairs =>
    {
        await artifacts.SuspendAsync(affectedRequests);
        await loader.ReplacePluginsAsync(pairs);
    },
    admitCandidate: candidate =>
    {
        foreach (var name in candidate.Specifiers)
            if (candidate.GetContribution(name) is { } contribution)
                CheckApplicationContract(contribution);
        return ValueTask.CompletedTask;
    },
    cancellationToken: cancellationToken);
await artifacts.ResumeAsync(affectedRequests);
```

`CheckApplicationContract` 是应用自己的政策；检查输出时读取 invocation 的 `Result.ResultSchema`。准入失败或切换前取消会退出候选、保留旧映射。切换失败继续使用既有显式恢复规则，只在确认提交或恢复成功后恢复合同。

准入完成即关闭视图，失败也一样。不要把插件、codec、delegate 或 Type 留作永久目录，应复制所需 JSON Schema 和字符串。视图不能使应用已捕获的 CLR 对象失效。工厂和 Schema 准备执行作者代码，其副作用不在回滚保证内。稳定启动、安装包重启升级及 native 资源所有权保持不变。
