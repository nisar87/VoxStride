using VoxStride.Core.Tools;

var caller = args.FirstOrDefault(a => a.StartsWith("chrome-extension://", StringComparison.Ordinal)) ?? "";
return await NativeHostRelay.RunAsync(caller, Console.OpenStandardInput(), Console.OpenStandardOutput());
