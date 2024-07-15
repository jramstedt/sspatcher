using System.Reflection;
using SystemShockPatcher;

const int ERROR_NO_ARGS = 0x01;

// See https://aka.ms/new-console-template for more information
if (args.Length < 2) {
    var assembly = Assembly.GetEntryAssembly();
    var name = assembly?.GetName().Name;
    
    Console.WriteLine($"{name} v{assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion}\n");

    Console.WriteLine("Usage:");
    Console.WriteLine($"\t{name} <in> [<in>]... <out>");
    return ERROR_NO_ARGS;
}

foreach (var inputPath in args[0..^1])
    ResManager.Read(inputPath);

ResManager.Save(args[^1]);

return 0;