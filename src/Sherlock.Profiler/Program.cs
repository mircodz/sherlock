using Sherlock;
using Sherlock.Profiler;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var debugger = new SherlockDebugger();

        // No arguments - start interactive mode
        if (args.Length == 0)
        {
            await debugger.RunAsync();
            return 0;
        }

        // Handle special flags
        var interactive = false;
        var argList = args.ToList();

        if (argList.Contains("-i") || argList.Contains("--interactive"))
        {
            interactive = true;
            argList.Remove("-i");
            argList.Remove("--interactive");
        }

        if (argList.Count == 0)
        {
            await debugger.RunAsync();
            return 0;
        }

        // Check if first argument is a process ID (attach mode)
        if (int.TryParse(argList[0], out var pid))
        {
            if (interactive)
            {
                Console.WriteLine("Interactive attach mode not supported. Starting regular interactive mode after attach.");
                await debugger.RunDirectAttachAsync(pid);
                await debugger.RunAsync();
            }
            else
            {
                await debugger.RunDirectAttachAsync(pid);
            }
            return 0;
        }

        // Otherwise treat as executable to spawn
        var executable = argList[0];
        var arguments = argList.Count > 1 ? string.Join(" ", argList.Skip(1)) : null;

        if (interactive)
        {
            await debugger.RunInteractiveWithSpawnAsync(executable, arguments);
        }
        else
        {
            await debugger.RunDirectSpawnAsync(executable, arguments);
        }

        return 0;
    }
}