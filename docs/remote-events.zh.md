# 向 Remote 监听器转发选定事件

[English](remote-events.md) | 中文

声明要暴露的事件，由 Host 选定转发，再在 Client 所有者下注册监听。固定 DSH 流程使用 `$events` 与 `$events/result`，不会自动公开所有应用事件或 DSH 产品事件。

## 准备两端

使用匹配的 Composition 包、生成式事件声明和显式 STJ metadata。网络 Host 还需要现有 Registry、Gateway、授权 Connection 与 ASP.NET 适配器；Client 需要 Cordis Context、Registry 和借用 Connection。先完成 [Remote Host 接线](authoring.zh.md#登记调用与撤销合同)。

以下片段属于应用的一部分。`Agent` 及其 Id／Context、请求与通知 DTO、JSON context、业务处理和 `localContinuation` 均由应用提供，普通 JSON 边界类型注册到 AppJson。期望远端 waterfall 前，须通过[scope 路由](authoring.zh.md#注册-scope-与-peer-事件路由) API 注册 subject 所属的 scoped 生命周期。

## 声明与选择

为公开 delegate 标记 RemoteEvent。普通通知保留参数列表，异步 scoped waterfall 保留 `next()` 结果合同：

```csharp
[assembly: TypertContext("agent", typeof(string))]

[RemoteEvent("app/decision", typeof(AppJson))]
public delegate Task<RemoteOptional<string?>> DecisionRequested(
    [RemoteLookup("agent", "agentId", typeof(string))] Agent agent,
    DecisionRequest request,
    Func<Task<RemoteOptional<string?>>> next,
    CancellationToken signal);

[RemoteEvent("app/changed", typeof(AppJson))]
public delegate void ApplicationChanged(Change change);
```

request 承载业务数据，投影的 agent 和 signal 不序列化 CLR 资源。在 Host 所有者执行域选择转发，并一次性提供 subject 投影：

Host 转发与调用：

```csharp
var selected = new[] {
    ApplicationChangedTypert.Definition,
    DecisionRequestedTypert.Forward(agent => new(agent.Context, agent.Id)),
};
gateway.RegisterRemoteEvents(owner,
    TypertRemoteEvents.FromCordis(owner, selected), new TypertRemoteEventHost(home));
var artifact = TypertArtifacts.GenerateEvents(selected);

var decision = await DecisionRequestedTypert.InvokeAsync(
    caller, agent, request, localContinuation, cancellationToken);
```

业务调用传入 subject，不传显式 wire 身份。辅助接口使用 `Scope.Target(agent, agent)`，转发时保留原始 subject。没有 scope key 的 receiver 通过 next 留在本地；带 key 的 carrier 若缺少 subject 或 subject 不同，在投影前拒绝。普通过滤、continuation 和来源归属保持有效。

## 在 C# 中监听

<a id="c-监听"></a>

在所有者执行域内注册 Client 监听，caller 与 Client owner 须共享根。agent receiver 先通过 `registry.RegisterClientContext(owner, "agent", adapter)` 安装应用适配器，其同步 Resolve 将传入身份映射为借用 Context 或 `TypertResolvedContext.Owned`；出站 scoped 调用复用同一适配器的 Identity。下方假定已安装该适配器：

```csharp
TypertRemoteEventsClient events = null!;
await owner.RunAsync(ctx =>
{
    events = new TypertRemoteEventsClient(ctx, registry, connection);
    ApplicationChangedTypert.On(events, caller, change => Refresh(change));
    DecisionRequestedTypert.On(events, caller, async (agent, request, next, signal) =>
    {
        if (!CanHandle(request)) return await next();
        return new RemoteOptional<string?>(await AskAsync(agent, request, signal));
    });
    return Task.CompletedTask;
});
try
{
    await events.RunAsync(host => OnReady(host.Home), generationCancellation);
}
finally
{
    await events.DisposeAsync();
}
```

RunAsync 管理一代连接，持续到取消或断开，重试由外层连接控制器负责。监听注册归 caller 的 Fiber，事件 Client 借用 Context、Registry 和 Connection，应先于借用资源释放。

监听返回 RemoteEventResult<T>，`await next()` 将 continuation marker 与任何 T 值分开。RemoteOptional<T> 独立表示允许的 undefined 与显式 null。Client 回调中的 agent 是解析后的 Client Context，不是重建的 Host Agent 对象。

## 生成独立监听器

使用 `emit-dotnet --service <event-name-or-delegate-name>` 和既有模型／客户端项目属性，在编译前生成类型化 On 辅助接口及数据。模型独立 Web 路线使用 CordisTypertWebModel 与显式引用模型，输出 `cordis/typert/web/remote.mjs` 的 `createEvents`、声明及共享 Connection 资产。生成不需要运行 Host 或提供者 DLL，转发仍要求 Host 选择。

普通参数、request 和 continuation 值是 Host 输出，handler reply 是 Host 输入。集合元素空性来自声明，包括可空列表与字典值；向 STJ 注册普通 CLR 类型，不手写第二份 Schema。独立后端拒绝不能复现的 serializer 行为，见[生成支持范围](development.zh.md#生成后端支持范围)。

## 在 TypeScript 中监听

<a id="typescript-监听"></a>

下方生成模块以 events.mjs 提供，传入现有 owner、Connection 与 adapter source，由应用回调提供 UI 与业务动作：

```ts
import { createEvents } from './events.mjs'
const events = createEvents(owner, connection, adapters)
events.on(caller, 'app/decision', async (request, next) => {
  if (!canHandle(request)) return next()
  return ask(request.agent, request, request.signal)
})
await events.run(generationSignal, host => onReady(host.home))
```

每次调用重新读取 `adapters.getClient('agent')`。resolver 返回借用的 Cordis Context、typertOwnedValue 的 owned value 或 undefined。`next()` 执行 Client 剩余监听链，没有答案时以 continuation 委托 Host；它不是将 Host 后续返回值暴露给浏览器的往返。

浏览器包通过 `/events` 导出标准事件，交给 createManagementClient 复用先订阅后读取的恢复流程。使用同一代取消信号，在页面所有者结束时关闭并释放事件 Client。

## 结束与证据边界

<a id="结算与证据边界"></a>

resolver 与 release 回调同步执行，owned cleanup 不应抛错。解析缺失或失败时委托 Host continuation。借用值不被释放，owned value 在 handler 与结果回复完成后最多释放一次。取消传递到 handler，但不提前释放其 Context；Client 与 Connection 的释放等待活动 waterfall 工作。

断开移除该 delivery，不合成 next，待处理调用可到达后续连接代际。来源撤销关闭流并取消待办，旧身份和重复回复不能结束或复活当前 delivery。回复区分 result、next 与 rejection，保留 name／message／code／details；通知不承诺持久重放。

协议与 owned-value 源码位于固定 DSH 的 `packages/typert/protocol` 和 `packages/api/gateway`，Host 转发位于 `packages/api/remotes`。[upstream.lock.json](../upstream.lock.json)选择当前基线，[兼容说明](compatibility.zh.md)记录适配，[验证记录](validation.zh.md)标明已完成的消费者。选定事件转发不代表完整 DSH 产品／SDK 互通。
