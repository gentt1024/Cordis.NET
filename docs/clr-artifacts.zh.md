# 安装与替换 CLR 插件制品

[English](clr-artifacts.md) | 中文

直接加载完整稳定制品，保留共享合同，并通过所属生命周期退出。新进程或 collectible AssemblyLoadContext 不要求复制另一份 bundle。动态 CLR 加载需要普通 .NET 运行时。

## 从源码目录运行插件

使用开发版 Cordis.NET.Clr，将 `cordis.plugin.json` 放在入口工程旁。如果根目录有多个工程，在该文件的 `project` 字段指定相对入口工程路径。入口工程在根目录下的子目录时，在工程里将 `CordisLocalPluginRoot` 设为源码根。插件入口选择单一目标框架。

运行 `dotnet build -c Debug`，或在 IDE 中选择同一配置。完成目标收集 SDK 运行文件、SDK 生成的独立 PDB、deps 文件、私有依赖、模型和已声明的 Web 输出。其他资产用 `CordisLocalFile` 声明，并设置相对 `TargetPath`。将必须成功的生成步骤列入 `CordisLocalOutputDependsOn`，这样生成失败就不会发布该模块的新输出。纯 CLR 工程只需要 .NET SDK。

`ClrLocalProject.QueryAsync(root, configuration)` 查询包 ID、入口工程与目标框架，不执行构建。`ResolveOutput()` 核验该配置上一次完成的运行输出。MSBuild 调用方可查询 `CordisGetLocalOutput`。输出缺失或损坏会得到诊断。模块完成回执只覆盖该工程。启动器须等整次构建成功退出后再启动。若自定义目标安排在模块完成之后，它可能在模块指针更新后失败。

## 通过运行中的 Host 登记本地源码

此入口用于受信的开发工作区。用接收 `LocalSourcePolicy` 的 `DotnetPluginToolchain.OpenForRunAsync` 打开 profile，将同一个工具链交给管理服务，并用 `PluginManager.LocalSourceSubject` 从已认证的调用上下文取得稳定主体。相对路径以策略指定的工作区为基准，绝对路径也须在该工作区内。Host 可以自动放行写入者受信的独占工作区，也可以逐项目决定。默认 Host 关闭在线本地源码能力。

| Host 的决定 | 可用事实 |
| --- | --- |
| Read | 在读取源码内容前核对主体、profile、源码根和配置。披露已保存路径也需要此权限。 |
| Evaluate | 在启动 MSBuild 前核对确切入口项目和配置。包 ID 和目标框架在求值完成后取得。 |
| Register | 核对求值身份、完成输出目录及回执标识，或确认项目尚无成功输出。 |
| Activate | 核对 Host 配置选定的完整产物。重开、刷新以 Host 自身身份（subject 为 null）重新询问当前策略。 |

调用 `inspectLocalSource`，传入来源与构建配置。用原请求、返回的 `inspectionId`、新的 `requestId` 和预期 `enabled` 值调用 `registerLocalSource`。本次检查绑定主体、profile 修订、入口项目、配置、求值身份和所选输出，在当前 Host 生命周期内有效。发生冲突后重新检查。首次构建前可以登记为停用，正常 Build 之后再启用。损坏的回执会报错。分别读取持久选择和运行结果，已保存的代码变更可能需要重启。应用结果描述本次请求。后续文件监听刷新可能改变活动条目，断线重连时请重新查询。移除来源会保留作者目录。

若响应丢失，以同一主体调用 `waitForLocalSource`。null 表示当前没有匹配的活动请求。先用 `readRegisteredLocalSource` 查持久来源、用 `listPlugins` 查运行状态，再决定后续操作。`cancelLocalSource` 能取消准备阶段；进入提交阶段后可能返回 too-late。

Host 选择 dotnet 工具和查询参数。`-noAutoResponse` 排除自动响应文件；项目 imports、属性函数和 SDK 配置仍以 Host 账户权限求值。授予 Evaluate 或 Activate 前，要确认这些输入及产物的写入者受信。回执校验消费的字节，保存的链接标识下次需要重新授权的来源。若共享写入者不受信，需要另外控制快照或执行环境。[Microsoft 的 MSBuild 安全建议](https://learn.microsoft.com/visualstudio/msbuild/msbuild-security-best-practices)

## 准备包与 Host

<a id="标准包工作流"></a>

构建普通插件 NuGet 包，包含 `cordis.plugin.json`、私有依赖、资源及所需 Worker 制品；应用数据放在代码目录外。准备使用选定的 .NET SDK 和包含完整依赖集的显式源。[ManagedPlugin](../examples/ManagedPlugin/README.zh.md) 与 [ManagedApplication](../examples/ManagedApplication/README.zh.md)提供包和 Host。

创建 `ClrModuleResolver(sharedContracts: [...])`，传入 Host 的精确附加合同程序集，再创建 `DotnetPluginToolchain(profileDirectory, resolver, sources)`。将 `toolchain.Bundles` 交给 `ProfileLaunch.LocalBundles`，使用 `session.ConfigurationOperations`。toolchain 管理获取、准备、文件回执、发布和保留目录，应用管理源选择、构建审批与业务就绪。

## 安装、发布与启动

1. 以精确版本和显式构建许可调用 InstallPackageAsync。准备阶段在独立 `.cordis/work` 输出中执行一次 SDK 部署 publish，并以插件 DLL 本名写入 AssemblyDependencyResolver 使用的部署依赖清单。产品包装器可在返回准备结果前完成自己的 metadata。
2. toolchain 发布将完整输出移入 `.cordis/packages/<name>/<version>`，在相邻 `.<version>.files.json` 记录文件集与 SHA256 摘要，再验证和注册。回执位于制品树之外，不覆盖已有版本目录或回执；准入和完整准备／发布树比较仍属于安装。
3. 启动在注册代码前核对回执与身份，直接加载稳定目录并复用模块 lease。重复进程验证但不复制；部署缺失、不完整或被修改时失败，不重写回执使其通过。

回执在验证点检测变化，不替代发布者认证，也不提供恶意文件系统沙箱。由 toolchain 之外管理的外部目录通过低层解析器 API 加载，部署所有者须提供完整稳定文件；已安装包的启动仍要求自己的回执。

## 升级、移除与重新安装

升级已安装的 Profile 所有包时，先准备并保存新精确版本，再报告 `restart-required`。当前进程保留旧代码路由，普通配置编辑仍作用于旧实现；监听到的选择变化可在重启前启用或禁用它。重启读取已保存版本，启动失败不自动恢复旧版本。同版本重复安装与替换 installation 所有包仍拒绝。

RemovePackageAsync 取消选择、等待自有 Fiber 结束，再移除 Profile 依赖和解析器引用。改变选择前通过 GetLoadedModulesAsync 检查，直接或外部 Fiber 导致 `restart-required` 和 `unmanaged-plugin`。toolchain 包装器须转发该查询与 GetRetainedDirectories。撤销后持久化失败时，可重试移除以完成依赖编辑。

移除后可在同会话重新安装并加载新依赖闭包。同版本复用要求有效回执和完全相同的准备内容，另一棵有效树返回 `package-moved`，损坏仍失败。保留文件本身不允许重新启用，不要修改已安装树来强行匹配。

逻辑移除为保留引用、延迟加载、Worker 和其他进程保留字节。PackageChange.Residuals 报告保留版本，包括旧活动版本和等待重启的版本，不表示物理删除。

## 离线删除保留文件

先停止所有 Host、Worker 和其他消费者，再调用 `DotnetPluginToolchain.DeleteRetainedArtifactAsync(profileDirectory, name, version)`，或使用 CLI：

```console
cordis delete-retained <profile> <name> <exact-version-or-graph>
```

Profile 仍引用精确版本时拒绝删除，CLI 还拒绝运行中的标准 CLI Host。API 在检查引用与删除期间持有既有 `package.json.cordis-lock` 写锁，拒绝冲突的协调写入者；manifest 损坏或不可读时保留制品。部署所有者须排除忽略该协议的外部消费者与写入者。失败报告剩余路径，不进行自动扫描或 GC 触发删除。

## 开发时替换实现代码

<a id="开发与实现-hmr"></a>

每次代码修订发布一个完整目录，在消费者使用期间保留旧输出不变。注册 ClrModuleDefinition，再通过 Loader 替换回调调用 ReplaceAsync。[CLR Probes](../examples/Probes.Clr/Program.cs)使用不同的已准备 V1、V2 目录。

LoadMode 默认为 StableDirectory。仅对之后须原地重建的外部开发目录选择 ShadowCopy；准备前先完成并静止整个构建，复制无法为活动构建创建一致快照。源码和副本不得重叠，bundle 包括私有／原生依赖、Worker 和资源。

ClrUnloadObservation.LoadDirectory 标明退出实例所用目录，稳定加载时 ShadowDirectory 为 null，TryDeleteShadow 不删除稳定源码。显式副本也须等所有消费者停止后再删，托管回收不证明这一前提，原生锁仍可能延迟删除。业务退出、激活、卸载请求、回收和删除分别观察，生产不强制 GC，不同 ALC 不保证原生静态状态独立。

## 检查已准备的替换候选

<a id="检查已经准备的替换候选"></a>

通过 admitCandidate 在撤销前检查实际合同。可选回调取得含别名和非活动导出的注册名称；无 IClrTypertModule 时 GetContribution 为 null，别名共享模块、插件和缓存贡献，普通解析仍看到已发布代际。

下方集成片段展示切换成功路径，解析器、Loader、Typert loader（`artifacts`）、已准备替换定义和受影响的精确 Loader 请求名均须已存在：

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

CheckApplicationContract 是应用策略，输出读取 invocation 的结果 codec ResultSchema，沿用正常惰性投影与诊断。长期保留时复制 JSON Schema 和字符串，不保留插件、codec、delegate 或 Type。

准入失败或切换前取消会退出候选并保留旧映射。切换失败须检查既有恢复结果：仅在确认提交或成功恢复后恢复受影响合同，并保留原失败；该片段不实现应用失败分支。准入结束后 view 关闭，已捕获 CLR 对象仍是有效对象。factory 和 Schema 准备执行可信作者代码，其副作用不在回滚内。已安装包升级仍要求重启，不使用这条开发替换路线。
