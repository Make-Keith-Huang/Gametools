using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Diagnostics;

/// <summary>Frozen allowlisted UTF-8 payloads. Export never rereads logs, settings, databases or source snapshots.</summary>
public sealed class SupportBundlePreview
{
    private static readonly string[] EntryNames = ["versions.json", "metrics.json", "events.jsonl", "manifest.json"];
    private SupportBundlePreview(Dictionary<string, string> entries) => Entries = new ReadOnlyDictionary<string, string>(entries);
    public IReadOnlyDictionary<string, string> Entries { get; }

    public static SupportBundlePreview Create(DiagnosticMetrics metrics, IReadOnlyList<string> recentEvents,
        Version applicationVersion, int configurationSchema)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(recentEvents);
        ArgumentNullException.ThrowIfNull(applicationVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(configurationSchema);
        // Six stages exist; freeze bounded typed values now, not at export time.
        var frozen = metrics with { Session = metrics.Session with { Stages = metrics.Session.Stages.Take(6).ToArray() } };
        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["versions.json"] = JsonSerializer.Serialize(new { Application = applicationVersion.ToString(), Runtime = Environment.Version.ToString(), ConfigurationSchema = configurationSchema }),
            ["metrics.json"] = JsonSerializer.Serialize(frozen),
            ["events.jsonl"] = string.Join("\n", recentEvents.Skip(Math.Max(0, recentEvents.Count - 100)).Select(ProjectEvent)) + "\n"
        };
        entries["manifest.json"] = JsonSerializer.Serialize(new
        {
            FormatVersion = 1,
            Entries = EntryNames,
            Payloads = entries.Select(entry => new
            {
                Name = entry.Key,
                Bytes = Encoding.UTF8.GetByteCount(entry.Value),
                Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entry.Value)))
            }).ToArray()
        });
        if (entries.Values.Sum(Encoding.UTF8.GetByteCount) > 512 * 1024)
        { throw new InvalidOperationException("Diagnostic bundle exceeds its size limit."); }
        return new SupportBundlePreview(entries);
    }

    /// <summary>Explicit local export, never overwrite. Progress reports fixed entry names only.</summary>
    public Task ExportAsync(string destination, bool consent, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (!consent)
        { throw new InvalidOperationException("Review and confirm the support bundle before exporting."); }
        var finalPath = Path.GetFullPath(destination);
        if (new Uri(finalPath).IsUnc)
        { throw new ArgumentException("Select a local destination.", nameof(destination)); }
        return Task.Run(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partialPath = Path.Combine(Path.GetDirectoryName(finalPath)!, ".gamelingo-bundle-" + Guid.NewGuid().ToString("N") + ".partial");
            var created = false;
            try
            {
                await using (var stream = new FileStream(partialPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                {
                    created = true;
                    using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
                    foreach (var entry in Entries)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await using var output = archive.CreateEntry(entry.Key, CompressionLevel.Optimal).Open();
                        await output.WriteAsync(Encoding.UTF8.GetBytes(entry.Value), cancellationToken).ConfigureAwait(false);
                        progress?.Report(entry.Key);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(partialPath, finalPath, overwrite: false);
            }
            finally
            {
                if (created && File.Exists(partialPath))
                { File.Delete(partialPath); }
            }
        }, cancellationToken);
    }

    private static string ProjectEvent(string json)
    {
        var safe = new Dictionary<string, object> { ["Kind"] = DiagnosticEventKind.Unspecified.ToString() };
        try
        {
            if (json.Length > 16 * 1024)
            { return JsonSerializer.Serialize(safe); }
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            { return JsonSerializer.Serialize(safe); }
            if (root.TryGetProperty("Timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var instant))
            { safe["Timestamp"] = instant.ToUniversalTime(); }
            if (root.TryGetProperty("Properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                CopyEnum<DiagnosticEventKind>(properties, safe, "Kind");
                CopyEnum<PipelineStage>(properties, safe, "Stage");
                CopyEnum<FailureCode>(properties, safe, "ErrorCategory");
                foreach (var name in new[] { "SessionId", "CorrelationId" })
                {
                    if (properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.TryGetGuid(out var id) && id != Guid.Empty)
                    { safe[name] = id; }
                }
            }
        }
        catch (JsonException) { }
        return JsonSerializer.Serialize(safe);
    }

    private static void CopyEnum<T>(JsonElement properties, Dictionary<string, object> safe, string name) where T : struct, Enum
    {
        if (properties.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
            Enum.TryParse<T>(value.GetString(), out var parsed) && Enum.IsDefined(parsed) && value.GetString() == parsed.ToString())
        { safe[name] = parsed.ToString(); }
    }
}
