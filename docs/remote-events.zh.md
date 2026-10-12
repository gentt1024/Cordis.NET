# Remote 事件

[English](remote-events.md)

固定 DSH 的事件路径是 `$events` 与 `$events/result`。Cordis 事件声明决定监听签名，Host 选择需要转发的兼容声明。普通 void 事件保留原参数列表；scoped 异步 waterfall 保留同结果类型的 `next()`，并把 Host agent subject 投影为客户端解析出的 Context。平台不会自动选入 DSH 的产品事件。

## 声明与选择

带 `RemoteEvent` 的公开委托保存在现有共同声明模型中。生成器从这些事实生成 Host 描述和类型化监听，不建立第二种事件模型格式。

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

`AppJson` 提供请求、结果和通知参数的源生成 JSON 元数据。request 只含业务字段；`agent`、`signal` 是保留的投影字段，不序列化 CLR 资源。C# 用普通类型化委托参数表达 TS 的 receiver／request／next。Host 在选择转发源时提供一次 subject 投影：

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

业务调用传 subject，不传 wire 身份。转发绑定从 subject 得到 Context 与 `agentId`，核对 receiver 就是该 subject，只发送已声明的 JSON 数据。过滤、continuation 和源所有权继续由现有 Cordis 机制承担。

## C# 监听

```csharp
await using var events = new TypertRemoteEventsClient(owner, registry, connection);
ApplicationChangedTypert.On(events, caller, change => Refresh(change));
DecisionRequestedTypert.On(events, caller, async (agent, request, next, signal) => {
    if (!CanHandle(request)) return await next();
    return new RemoteOptional<string?>(await AskAsync(agent, request, signal));
});
await events.RunAsync(host => OnReady(host.Home), generationCancellation);
```

外围 connection controller 拥有恢复策略。`RunAsync` 表示一个代际，不添加重试策略。Client 借用 Registry、Connection 和 Context，监听注册属于调用 Fiber。

类型化 waterfall 监听返回 `RemoteEventResult<T>`，将 continuation 标记与所有 `T` 值分开。`return await next()` 保留该标记；普通 `T` 隐式转换为业务答案。声明允许 undefined 时使用 `RemoteOptional<T>`。这样 continuation、undefined 与显式 null 各自独立，不通过用户代码中的内部控制异常模拟。

现有 `emit-dotnet` 的 `--service` 也接受选定事件名或委托元数据名。`CordisTypertClientModel`、`CordisTypertService`、`CordisTypertClientNamespace` 和 `CordisTypertClientName` 在编译前从保存的共同模型生成类型化 `On` 帮助类及独立数据。后端序列化限制仍然适用，不能复现的 Host serializer 不会被悄悄替换为另一种策略。


集合元素空性来自事件声明，包括 `List<string?>?` 和 `Dictionary<string, T?>`。作者在 JSON context 注册普通 CLR 元数据（`typeof(List<string>)`），不另写一份 Schema。源码生成监听、保存模型生成监听和 TS 监听使用同一组事实；非空元素仍拒绝 null。委托采用 namespace 限定身份，互不相关的同名事件委托可以共存。

## TypeScript 监听

```ts
import { createEvents } from './events.mjs'
const events = createEvents(owner, connection, adapters)
events.on(caller, 'app/decision', async (request, next) => {
  if (!canHandle(request)) return next()
  return ask(request.agent, request, request.signal)
})
await events.run(generationSignal, host => onReady(host.home))
```

每次调用都读取 `adapters.getClient('agent')`。其同步 resolver 返回借用的 Cordis Context、`typertOwnedValue` 创建的 owned 值，或 undefined。owned-value 共享 symbol 与固定 DSH 相同，不另存 registry 或永久身份表。生成式 emit 监听保留各个原参数，waterfall 监听将 `agent` 与 `signal` 投影到 request。

浏览器模块包通过 `/events` 导出标准平台事件。`createManagementClient({ modules, events })` 复用现有恢复控制器，先订阅再读取完整 state／graph，不重放写入。旧通知 Remote 与 `/notifications` 导出退出。

## 结算与证据边界

resolver 和 release 保持同步。缺少或失败的解析继续 Host 的 continuation。borrowed 值不销毁；owned 值在 handler 与回复结算之后最多释放一次。取消会通知 handler，但不会提前释放其 Context。Client 和 Connection 释放会等待活动 waterfall。

断线移除该次交付，不合成 `next`，待决调用可以投递给后续代际。源撤销关闭流并取消待决工作。旧 client 身份不能结算当前交付；重复的已完成回复不能恢复调用。emit 不保证持久重放。waterfall 回复区分 result、next 和 rejection，保留拒绝的 name、message、code、details。

源码对应固定 DSH `639ed015397290b3745d163aafe02ffee4aa3f84` 的 `packages/typert/protocol/src/types.ts`、`owned-value.ts`、`packages/api/gateway/src/client/remote-events.ts`、`stream-protocol.ts`，以及 `packages/api/remotes/src/remote-events.ts` 的转发选择。这条路径本身不证明全部浏览器 scoped、直接 Gateway 的 operatorPeer fallback、所有模型后端或全部 DSH 产品／SDK 互通。验证记录单独标识实际候选及已执行消费者。
