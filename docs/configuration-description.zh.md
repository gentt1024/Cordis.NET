# 声明配置与实时字段

[English](configuration-description.md) | 中文

定义一个 validator，描述它接受的字段，并显式选择可实时更新的值。descriptor 在同一配置合同上增加表单与导出，不创建第二个 validator。

## 选择声明 API

Plugin<T>、ConfigSchema<T> 与配置引用使用 Core，ConfigObject<T>、Loader、Settings 和导出／发现辅助接口使用 Composition。以下 API 已包含在发布的 `0.2.1-alpha.1` 中，包与工具须匹配。这是普通插件配置，与 [Remote 输入默认值](authoring.zh.md#remote-输入的静态数据默认值)不同。

完整数据对象使用 ConfigObject<T> 与完整字段列表，每个 Field 关联原始键、descriptor 和纯值投影；辅助接口提供实时绑定、普通比较及包含默认值的完整简化。每个字段都须声明。嵌套实时路径、不透明值或自定义相等／转换使用 ConfigSchema<T>，标量可直接使用 Plugin<T>.Config。

Plugin<T>.Config 仍是校验权威，同时提供 Config 与 Configuration 时须指向同一 validator 委托。注册一起捕获 validator、descriptor 和投影。自定义 IPlugin 适配器可通过 IConfigurationPlugin.CaptureConfiguration 提供捕获声明。

## 运行实时字段示例

使用 .NET 10 SDK 创建控制台应用：

```console
dotnet new console -n ConfigExample -f net10.0
dotnet add ConfigExample package Cordis.NET.Composition --version 0.2.1-alpha.1
```

将 ConfigExample/Program.cs 替换为下方完整程序。它校验 limit／label，让 limit 实时更新，把 label 作为普通重启字段，不需要 Node 或网络 Host。

Program.cs：

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

var schema = ConfigObject<Settings>.Create(Validate)
    .Field("limit", ConfigDescriptor.Number().Default(1).Volatile(), value => value.Limit)
    .Field("label", ConfigDescriptor.String().Default("worker"), value => value.Label)
    .Build();

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

运行 `dotnet run --project ConfigExample`。第一次实时更新输出 `live=2, initial=1`，引用改变而该次激活的初始 record 不变；修改 label 会重启并输出 `old=2, restarted=3`；替换实现输出 `retired=4, replacement=4`。退出的引用停留在所属激活最后提交的值。

直接 Fiber.Update 使用 Loader 保存 hook 与 WithSimplify，savedRaw 包含两个字段；文件树会写入该数据。兼容实时更新不调用普通 update／save hook，`noSave: true` 只跳过持久化，不跳过生命周期。静态 Native AOT 的代码变化须重新发布，运行时 ReplacePluginAsync 不是 AOT 代码加载器。

## 保持校验与描述一致

descriptor 的键、形状和默认值须与 validator 一致。POCO 的 WithOrdinaryEquality 比较每个普通字段、排除实时字段；WithSimplify 返回可以再次校验的完整原始数据。仅当 validator 能恢复相同值时才可省略默认值。结构 map 可使用 descriptor 比较／简化。

每个固定实时字段恰好需要一个投影，缺失、多余或被阻断的投影会被拒绝。库不反射 POCO 字段，也不证明自定义校验实现了描述中的默认值。实时投影是分离的不可变原始值、数组或字符串键 map；相同快照复用值对象，重复的无环分支分别复制。函数、不透明实例和环不能成为实时快照，普通配置仍可持有不透明值和递归图。

复合快照使用 object、IReadOnlyList<object?> 或 IReadOnlyDictionary<string, object?>，不期待重建任意类型化集合。GetConfigReference 的字符串重载选择一个成员，对象键列表重载选择嵌套路径；无参 GetConfigReference 和 WithVolatileValue 描述根值，通知路径为空字符串。以可回收插件类型为参数的引用可能保留该程序集。

## 描述类型化输入与 lazy 分支

以下片段复用前一程序的 Settings、Validate、Raw 和 schema 定义：

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

WithDescriptionData 将已验证 POCO 一次性投影为解析 lazy 描述所需的完整普通形状，不负责校验或持久化，WithSimplify 不替代它。map 输入使用实际交给 validator 的配置 hook 结果。

含 lazy 的 union 需要与 validator 分支选择一致的显式 selector，未使用分支不展开。缺少 selector 的 lazy union 被拒绝；带 selector 的 union 不能序列化，因为无法保存其回调，应保留作者声明用于重建。

兼容更新保留只读引用身份。被拒绝的原始输入保留在 Entry.Options.Config 和 Fiber.RawConfig，但有效配置与引用保持已接受值。force 更新仍处理部分释放和 patch Context，是否重新挂载取决于最终变化。Core 的 RetainRawConfiguration、TryPrepareConfigurationUpdate 和 Commit 是 Loader 集成 API，要求所属执行域，只允许一次新鲜提交，本身不通知或保存，不是并发属性更新 API。

## 显式应用元数据与独立导出

<a id="显式应用元数据与两条独立导出"></a>

WithMetadata 在 validator 旁增加渲染与约束提示：role、普通／本地化描述、badge、额外普通数据、hidden／disabled／collapse、链接／注释、min／max／step、pattern 源码／flags 与 loose 接受。实际规则和归一化仍由 validator 实现：

```csharp
var limitDeclaration = ConfigDescriptor.Number().Default(2).Volatile()
    .WithMetadata(new ConfigurationMetadata
    {
        Min = 0, Max = 10, Step = 2, Role = "slider",
        Descriptions = new Dictionary<string, string> { ["en"] = "Even count", ["zh"] = "偶数数量" },
        Badges = [new ConfigurationBadge("preview", "warning")]
    });
var passwordDeclaration = ConfigDescriptor.String().Optional()
    .WithMetadata(new ConfigurationMetadata { Role = "secret" });
```

Core 保存不可变无环注解，复制调用方容器，拒绝不透明对象／delegate。未知注解产生导出诊断，不能覆盖 required／default／实时规则；descriptor 可以保留未解析 lazy 回调，由普通配置解析负责执行。

ToSchemastery 导出可调用的 `{uid, refs}` 表单封套，ToJsonSchema 独立导出含 `$defs` 和 x-cordis metadata 的 JSON Schema 2020-12。两者保留图身份，不执行 validator 或 lazy builder。空 tuple／union／intersection 有可消费表单，tuple 沿用固定源码非严格开放尾部。数值边界直接投影，非零 minimum 下的 step、UTF-16 长度和 ECMAScript pattern 有明确 JSON Schema 限制；pattern 提示保留于 x-cordis，loose 恢复放宽接受。Complete 报告是否仍依赖运行时行为，Schema 文档不替代 transform、getter、分支决定或原生归一化。

## 嵌套 Settings 与源码编辑

<a id="嵌套-settings-与源编辑"></a>

通过会话 ConfigurationOperations 使用有序 ConfigurationSet／ConfigurationUnset 路径和新鲜源码 revision。它校验一个完整最终候选，再进行一次源码更新／协调，中间状态可以无效。对象 unset 恢复继承，数组 unset 移除元素，set 当前数组长度会追加；数组索引为规范非负十进制字符串。Saved、Applied 和恢复失败分别处理。

在同一操作上挂载 SettingsForms。下方是独立会话片段，不是仅有 limit／label 程序的扩展，假定活动 worker 声明有显式投影的实时 network.timeout 与 network.password 字段：

```csharp
var forms = new SettingsForms(ctx, operations, _ => new SettingsPolicy(["network"]));
var view = (await forms.DescribeAsync()).Value.Namespaces.Single(row => row.Ns == "worker");
using var timeout = JsonDocument.Parse("30");
await forms.MutateAsync("worker",
    [new SettingsPathOp("set", ["network", "timeout"]) { Value = timeout.RootElement },
     new SettingsPathOp("unset", ["network", "password"])], view.Revision);
var formSchema = (await forms.DescribeAsync()).Value.Namespaces.Single(row => row.Ns == "worker").Schema;
```

SettingsPolicy 筛选支持的实时表单，省去普通兄弟与 Host 隐藏路径，不能通过替换祖先绕过隐藏。秘密只写，替换祖先可能重置省略的秘密，因此不完整视图使用路径编辑。Describe 返回一致的分离快照，包含 base／user 值、数值 revision 和秘密存在路径；union／intersection 分支保守移除隐藏／秘密数据，秘密数组位置保留 null 占位。非实时路径使用新包装对象，实时边界保留 metadata；完整配置 Schema 可能包含默认值，需要单独授权。

读取会通过普通生命周期同步，可能执行插件代码。递归脱敏跟随已存在后继，缺失／null 容器停止遍历；无进展 lazy／union 环会省去该值并报告诊断，不返回未脱敏数据。Remote 结果与 revision 处理见 [Settings](settings.zh.md)。

## 不激活插件的发现

<a id="无需激活插件的声明发现"></a>

PluginConfiguration.Descriptor 提供捕获声明。使用获授权解析器和显式有序层，下方假定 configurationPath、plugin 与 layers 已存在：

```csharp
var catalog = await ConfigurationSchemaDiscovery.DiscoverAsync(
    configurationPath, new StaticModuleResolver().Register("worker", plugin), layers);
foreach (var entry in catalog.Entries)
    Console.WriteLine($"{entry.Source}: {entry.EntryId}: {entry.Diagnostic ?? entry.JsonSchema?.Format}");
```

DiscoverAsync 遍历原生 group／include，解析相对 include 路径，缺失文件使用 literal 初始数据而不写文件。它报告循环和 import 失败，保留仍有效的兄弟条目；禁用条目也提供声明，源码文件名消除位置 ID 歧义。自定义树载体产生明确的不支持遍历诊断；含 `__jsExpr` 的原生 group／include 整体保持不透明，包括其兄弟 path 数据。

发现不创建 Context 或 Loader，不调用 Apply、ResolveConfig、表达式 evaluator 或 lazy builder；模块 import 与可选 CaptureConfiguration 是可信作者代码，可以执行。发现不是沙箱或第二套校验引擎。[当前支持](development.zh.md#当前支持与发行状态)与[生成支持范围](development.zh.md#生成后端支持范围)分别说明发行／平台和模型后端边界。
