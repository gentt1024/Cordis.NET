# 可选配置描述

[English](configuration-description.md)

引用状态只保存脱离原配置的投影快照。完整有效配置和验证委托由 Fiber 持有；退役的 `ConfigReference<string>` 不会额外保留配置 POCO 的可收集类型。故意持有泛型实参属于插件的引用仍可能保留程序集。所有字段快照一次发布，但分别读取多个字段不构成事务。

原始 `__jsExpr` 传输节点对对象描述保持不透明。同名子字段不能掩盖父级表达式源文本变化。同源文本无需求值即可等价，文本变化走普通生命周期；明确声明整个节点为 volatile 时仍支持兼容的原地更新。

这些 API 属于 `0.2.0-alpha.1` 源码版本。源码版本不代表 NuGet 已发布这些能力。参见[发行说明](../CHANGELOG.md)。

`Plugin<T>.Config` 仍是验证权威。插件可以额外提供 `Configuration = new ConfigSchema<T>(validator, descriptor)`。同时提供两项时，必须使用同一委托。注册时一并捕获验证器、描述和显式字段投影。现有 `IPlugin` 实现无需新增成员；适配器可以转发 `IConfigurationPlugin.CaptureConfiguration()` 的捕获结果。

作者提供验证规则，并保证描述中的原始字段名、结构和默认值与验证器一致。每个标记的固定字段需要一个有效值投影。POCO 的 `WithOrdinaryEquality` 必须比较每个普通字段并排除 live 字段；`WithSimplify` 必须返回验证器能够再次读取的完整原始数据。结构化映射可以使用描述的普通比较和简化。应一起测试这些类型化适配器，包括普通字段变更和保存后的再次读取。

库一并捕获这些声明，拒绝缺失、多余或处于非法位置的投影，并原子发布不可变快照。库不会推断 POCO 字段、证明自定义验证器实现了声明的默认值，或执行另一套验证引擎。普通比较返回 false 时，Loader 使用普通生命周期处理更新。

下面的完整示例接收原始映射并生成类型化 record。Apply 从该 record 读取普通配置，通过引用读取 live 配置。全部生命周期操作都在所属执行域中运行。

```csharp
using Cordis;
using Cordis.Composition;
using System.Globalization;

static ConfigResult<Settings> Validate(object? raw)
{
    if (raw is not IReadOnlyDictionary<string, object?> map)
        return ConfigResult<Settings>.Failure("a configuration map is required");
    try
    {
        map.TryGetValue("limit", out var limitValue);
        map.TryGetValue("label", out var labelValue);
        int limit = Convert.ToInt32(limitValue ?? 1, CultureInfo.InvariantCulture);
        string label = labelValue is null ? "worker" : (string)labelValue;
        return limit > 0 ? ConfigResult<Settings>.Success(new(limit, label))
            : ConfigResult<Settings>.Failure("positive limit required");
    }
    catch (Exception error) when (error is FormatException or InvalidCastException or OverflowException)
    {
        return ConfigResult<Settings>.Failure("invalid limit or label");
    }
}

var schema = new ConfigSchema<Settings>(Validate,
    ConfigDescriptor.Object(
        ("limit", ConfigDescriptor.Number().Default(1).Volatile()),
        ("label", ConfigDescriptor.String().Default("worker"))))
    .WithVolatile("limit", value => value.Limit)
    .WithOrdinaryEquality((left, right) => left.Label == right.Label)
    .WithSimplify(value => new EntryOptions
    {
        ["limit"] = value.Limit, ["label"] = value.Label
    });

var references = new List<ConfigReference<int>>();
Plugin<Settings> CreatePlugin(string implementation) => new()
{
    Configuration = schema,
    Apply = (context, initial) =>
    {
        var limit = context.Fiber.GetConfigReference<int>("limit");
        references.Add(limit);
        Console.WriteLine($"{implementation}: {initial.Label}, limit={limit.Value}");
    }
};
static EntryOptions Raw(int limit, string label = "worker") => new()
{
    ["limit"] = limit, ["label"] = label
};

await using var root = new Context();
await root.RunAsync(async context =>
{
    var plugin = CreatePlugin("v1");
    var loader = new Loader(context, new StaticModuleResolver().Register("worker", plugin));
    await loader.Root.UpdateAsync([new() { Id = "worker", Name = "worker", Config = Raw(1) }]);
    await loader.WaitAsync();
    var entry = loader.Resolve("worker");
    var original = (Settings)entry.Fiber!.Config!;
    var old = references[^1];

    await entry.UpdateAsync(new() { Config = Raw(2) });
    Console.WriteLine($"live={old.Value}, initial={original.Limit}");

    await entry.UpdateAsync(new() { Config = Raw(3, "batch") });
    await loader.WaitAsync();
    Console.WriteLine($"old={old.Value}, restarted={references[^1].Value}");

    entry.Fiber!.Update(Raw(4, "batch"));
    await loader.WaitAsync();
    var savedRaw = entry.Options.Config;

    var beforeReplacement = references[^1];
    await loader.ReplacePluginAsync(plugin, CreatePlugin("v2"));
    Console.WriteLine($"retired={beforeReplacement.Value}, replacement={references[^1].Value}");
});

record Settings(int Limit, string Label);
```

第一次更新输出 `live=2, initial=1`：live 读取变更，有效 record 保留身份和原值。普通 label 变更触发重启，输出 `old=2, restarted=3`。直接调用 `Fiber.Update` 通过 `WithSimplify` 执行 Loader 的保存钩子；`savedRaw` 包含两个字段。文件型 tree 还会写入这些数据。兼容的 live 更新不执行更新/保存钩子。`noSave: true` 跳过持久化，仍执行普通生命周期。只有当验证器能重建相同值时，作者才可在 `WithSimplify` 中省略默认值。

代码替换创建新引用，输出 `retired=4, replacement=4`。`ReplacePluginAsync` 用于已准备好的运行时实现；CLR HMR 还会等待候选激活完成再替换旧实现。静态 Native AOT 应用必须重新发布才能更换代码。重启、替换或恢复后，旧引用保留其 activation 的最终提交值。

被拒绝的 live 输入保留在 `Entry.Options.Config` 和 `Fiber.RawConfig` 中，有效配置及运行中的引用保持最后一次接受的值。即使 raw 值相等，`force` 更新仍处理 partial disposal 和 patch-context；是否重新挂载由最终配置变化决定。

每个标记的固定对象字段必须恰好对应一个类型化投影。投影不能指向普通字段或不存在的字段。`WithVolatileValue()` 与无参数的 `GetConfigReference<T>()` 表示根级整值引用，其通知路径为空字符串。对象键列表重载支持嵌套路径，显示为转义的 JSON 指针；字符串重载表示一个准确的对象成员名。这些显式委托适用于静态 Native AOT 编写，不通过反射检查 POCO 属性。

兼容的原地更新保留只读引用的身份。引用值是分离的不可变原始值、数组或字符串键映射快照；相等快照保留原先的值对象，重复的无环分支分别复制。函数、不透明类实例和循环不能成为 volatile 快照。普通配置仍可包含不透明 CLR 值和递归图。图快照可使用 `object`、`IReadOnlyList<object?>` 或 `IReadOnlyDictionary<string, object?>`；可变集合类型不是只读快照类型。每次 activation 拥有独立引用状态：重启或替换后，旧引用保留最终值，新 activation 获取新引用。

`TryPrepareConfigurationUpdate` 为 live 候选执行一次既有配置钩子和捕获的验证器，然后检查普通有效值相等性及全部投影快照，不发布候选值。类型化 record 可提供 `WithOrdinaryEquality`；结构化映射使用捕获的对象描述。`ConfigurationUpdate.Commit()` 只能消耗一次候选，且仅在所属 activation 和基线仍当前时交换整个引用状态。它保留有效配置身份，不执行更新钩子、保存或重启。Loader 负责保留原始输入并在提交后向 owner 通知。直接调用 `Fiber.Update` 保留既有更新钩子、veto、保存和重启行为。

原始比较只深入声明的对象，并先跳过标记节点。对象缺失或为 null 时使用该对象的默认值；标量默认值不会归一化原始差异。数组、字典、元组、联合、交集、getter、transform 和 lazy 节点使用严格比较。共享节点递归在描述的回边处退回严格比较。严格相等保留缺失键与 `Undefined` 等价、null 区分、NaN 不等、不透明身份和循环行为。CLR 数值宽度适配源 Number 值域；DateTime/DateTimeOffset 使用 UTC 毫秒，Uri 使用规范地址，Regex 使用模式和选项，字节内存比较内容。

描述保留 required/optional、default、volatile、子节点和共享节点元数据，不实现第二套验证引擎。lazy builder 在捕获和原始比较时保持未展开。实际解析沿使用的数据路径遍历；缺失的 optional 值和空容器不会展开无限工厂图。已知的非法 volatile 位置在捕获时拒绝；实际使用的 lazy 路径在发布前检查。`Serialize()`/`Deserialize()` 保留图共享、递归边和元数据，不序列化验证器或回调。即使有限输入成功解析，仍含未物化 lazy builder 的图也不能序列化。

`Fiber.SimplifyConfiguration` 根据结构描述简化持久化数据。POCO 和 union 分支选择需要 `WithSimplify`，即显式、完整的类型化原始数据转换。该结果不再执行描述简化。schema 导出或目录不能替代捕获的运行时验证器和引用。Core 使用既有执行域，不依赖 Composition。

`RetainRawConfiguration`、`TryPrepareConfigurationUpdate` 和 `ConfigurationUpdate.Commit` 是 Loader 集成方法。须在所属 context 的执行域中调用，例如通过 `Context.RunAsync`。其他提交或 activation 变更后，过期候选不能提交；每个候选只能消耗一次。提交本身不通知也不保存。Loader 保留原始输入并在提交后向 owner 通知。只有普通字段的描述，在原始配置按 schema 等值时使用零引用快捷路径，不再次执行验证器。这些方法不是通用的并发属性更新 API。

CLR 适配继续按身份比较未知不透明对象。Composition 的已知原始表达式节点按源文本比较，不执行求值。任意类型化集合不会自动转换成对应的类型化不可变快照。如果泛型引用的值类型属于可回收 CLR bundle，宿主释放该引用前，它可能保留该 bundle。

当原始输入本身是 POCO，且描述包含 lazy 节点时，应提供 `WithDescriptionData`。它在验证后将有效值投影一次，得到解析描述所需的完整 plain 结构；它不是验证，也不是持久化。`WithSimplify` 不能替代它。其他情况下，映射输入采用配置钩子实际传给验证器的结果，不从有效 POCO 猜测结构。

含 lazy 分支的 union 需要显式分支选择器。选择器接收描述数据，返回要解析的已捕获子节点索引；它必须与验证器的分支选择一致。捕获和原始比较不执行它，未使用的分支保持未展开。没有选择器的 lazy union 会被拒绝。带选择器的 union 不能序列化，因为序列化无法保留该回调；须保留作者声明以便重新构建。不含 lazy 分支的普通 union 继续支持。

```csharp
var typedInputSchema = new ConfigSchema<Settings>(
    raw => raw is Settings value ? Validate(Raw(value.Limit, value.Label)) : Validate(raw),
    schema.Descriptor)
    .WithVolatile("limit", value => value.Limit)
    .WithOrdinaryEquality((left, right) => left.Label == right.Label)
    .WithDescriptionData(value => new EntryOptions
    {
        ["limit"] = value.Limit, ["label"] = value.Label
    })
    .WithSimplify(value => new EntryOptions
    {
        ["limit"] = value.Limit, ["label"] = value.Label
    });

static ConfigDescriptor Tree() => ConfigDescriptor.Object(
    ("children", ConfigDescriptor.Array(ConfigDescriptor.Lazy(Tree))
        .Default(Array.Empty<object?>())));
var selectedUnion = ConfigDescriptor.Union(
    raw => raw is IReadOnlyDictionary<string, object?> ? 0 : 1,
    ConfigDescriptor.Lazy(Tree), ConfigDescriptor.String());
```
