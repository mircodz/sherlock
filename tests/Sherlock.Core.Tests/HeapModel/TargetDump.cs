using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Sherlock.Core.Collection;
using Sherlock.Core.Tests.Common;
using Sherlock.Heap;
using Xunit;

namespace Sherlock.Core.Tests.HeapModel;

[CollectionDefinition(Name)]
public sealed class TargetDumpCollection : ICollectionFixture<TargetDump>
{
    public const string Name = "Target dump";
}

/// <summary>A heap dump of the test target, taken once and shared: a real heap, in the dump format of whichever
/// platform runs the tests. Where the platform can't dump a process in time, the tests skip and say why; the target
/// and anything it started are killed either way.</summary>
public sealed class TargetDump : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(60);

    private readonly TempDir _tmp = new();
    private readonly string? _path;
    private readonly string? _unavailable;

    public TargetDump()
    {
        string target = System.IO.Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Sherlock.TestTarget.exe" : "Sherlock.TestTarget");
        using Process process = Process.Start(new ProcessStartInfo(target) { RedirectStandardInput = true, RedirectStandardOutput = true })!;
        try
        {
            Task<string?> ready = process.StandardOutput.ReadLineAsync();
            if (!ready.Wait(Limit) || ready.Result != "ready")
            {
                _unavailable = "the test target didn't start";
                return;
            }
            // createdump writes to the target's stdout; a pipe nobody reads would eventually block it.
            _ = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
            string path = System.IO.Path.Combine(_tmp.Path, "target.dmp");
            Task dump = Task.Run(() => DumpCollector.Collect(process.Id, DumpKind.Heap, path));
            try
            {
                if (!dump.Wait(Limit))
                {
                    _unavailable = $"no dump after {Limit.TotalSeconds:0} s";
                    return;
                }
            }
            catch (AggregateException ex) when (ex.InnerException is DumpAnalysisException failure)
            {
                _unavailable = failure.Message;
                return;
            }
            _path = path;
        }
        finally
        {
            process.Kill(entireProcessTree: true); // createdump, if it's stuck, is the target's child
        }
    }

    public string Path
    {
        get
        {
            if (_path is null)
            {
                Assert.Skip($"Can't dump a process here: {_unavailable}");
            }
            return _path;
        }
    }

    public HeapDump Open() => HeapDump.Open(Path, System.IO.Path.Combine(_tmp.Path, "cache"));

    public void Dispose() => _tmp.Dispose();
}
