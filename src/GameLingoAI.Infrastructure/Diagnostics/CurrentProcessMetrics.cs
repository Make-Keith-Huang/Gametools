using System.Diagnostics;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>Samples aggregate counters of this process only, without reading memory contents or other processes.</summary>
public static class CurrentProcessMetrics
{
    public static Task<ResourceMetrics> SampleAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        long? privateBytes = null, workingSet = null;
        double? cpu = null;
        int? handles = null;
        try
        {
            using var process = Process.GetCurrentProcess();
            privateBytes = process.PrivateMemorySize64;
            workingSet = process.WorkingSet64;
            cpu = process.TotalProcessorTime.TotalMilliseconds;
            handles = process.HandleCount;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Do not export exception details; unavailable counters remain null.
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new ResourceMetrics(privateBytes, workingSet, GC.GetTotalMemory(false), GC.GetTotalAllocatedBytes(), cpu, handles);
    }, cancellationToken);
}
