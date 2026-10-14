using System.Reflection;
using System.Reflection.Emit;

var plain = Snapshot(MethodAttributes.Public, false);
var virtualRead = Snapshot(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot, false);
var abstractRead = Snapshot(
    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.Abstract,
    false);
var finalRead = Snapshot(MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final, false);
var writable = Snapshot(MethodAttributes.Public, true);
Require(
    plain.Contains("get=True set=False init=False getFlags=virtual=False,abstract=False,final=False,newSlot=False"),
    "plain getter");
Require(virtualRead.Contains("getFlags=virtual=True,abstract=False,final=False,newSlot=True"), "virtual getter");
Require(abstractRead.Contains("getFlags=virtual=True,abstract=True,final=False,newSlot=True"), "abstract getter");
Require(finalRead.Contains("getFlags=virtual=True,abstract=False,final=True,newSlot=False"), "final reuse-slot getter");
Require(writable.Contains("get=True set=True init=False"), "public setter");
Require(writable.Contains("setFlags=virtual=False,abstract=False,final=False,newSlot=False"), "setter flags");
Require(
    virtualRead
        .Split('\n')
        .Single(line => line.Contains("Method Int32 Read()"))
        .Contains("flags=virtual=True,abstract=False,final=False,newSlot=True"),
    "ordinary method");
Require(plain != virtualRead && plain != writable, "snapshot must reject extending a read-only API");
Console.WriteLine("API snapshot extension-boundary probes passed.");

static void Require(bool value, string contract)
{
    if (!value)
        throw new Exception(contract);
}

static string Snapshot(MethodAttributes flags, bool writable)
{
    var assembly = new PersistedAssemblyBuilder(new AssemblyName("Fixture"), typeof(object).Assembly);
    var type = assembly
        .DefineDynamicModule("Fixture")
        .DefineType("Codec", TypeAttributes.Public | TypeAttributes.Abstract);
    var getter = type.DefineMethod(
        "get_ResultSchema",
        flags | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        typeof(int),
        Type.EmptyTypes);
    var method = type.DefineMethod("Read", flags, typeof(int), Type.EmptyTypes);
    if ((flags & MethodAttributes.Abstract) == 0)
    {
        foreach (var member in new[] { getter, method })
        {
            var il = member.GetILGenerator();
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Ret);
        }
    }

    var property = type.DefineProperty("ResultSchema", PropertyAttributes.None, typeof(int), Type.EmptyTypes);
    property.SetGetMethod(getter);
    if (writable)
    {
        var setter = type.DefineMethod(
            "set_ResultSchema",
            MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
            typeof(void),
            [typeof(int)]);
        setter.GetILGenerator().Emit(OpCodes.Ret);
        property.SetSetMethod(setter);
    }

    type.CreateType();
    using var image = new MemoryStream();
    assembly.Save(image);
    return ApiSnapshot.Capture([Assembly.Load(image.ToArray())]);
}
