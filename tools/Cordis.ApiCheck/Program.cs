using System.Reflection;

var root = Environment.CurrentDirectory;
var baseline = Path.Combine(root, "docs", "public-api.txt");
var names = new[]
{
    "Cordis.Core",
    "Cordis.Composition",
    "Cordis.Extensions",
    "Cordis.Clr",
    "Cordis.Hosting",
    "Cordis.AspNetCore",
    "Cordis.JavaScript"
};
var current = ApiSnapshot.Capture(names.Select(Assembly.Load));
if (args is ["--accept"])
{
    File.WriteAllText(baseline, current);
    Console.WriteLine("Public API snapshot updated; review this diff before committing.");
    return 0;
}

if (!File.Exists(baseline) || File.ReadAllText(baseline).Replace("\r\n", "\n", StringComparison.Ordinal) != current)
{
    Console.Error.WriteLine("Public API changed. Review and run Cordis.ApiCheck --accept intentionally.");
    return 1;
}

Console.WriteLine("Public API matches checked-in snapshot.");
return 0;
