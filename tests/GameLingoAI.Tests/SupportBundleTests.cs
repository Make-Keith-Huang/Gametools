using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GameLingoAI.Core.Models;
using GameLingoAI.Infrastructure.Diagnostics;

internal static class SupportBundleTests
{
    public static async Task RunAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameLingoAI-bundle-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var stages = new List<StageMeasurement> { new(PipelineStage.Ocr, 10, 20, 30) };
            var metrics = new DiagnosticMetrics(DateTimeOffset.UnixEpoch, new(null, null, 1, 2, 3, 4, 5, 6, stages),
                new(null, null, 10, 20, null, null), 7, 8);
            var correlation = Guid.NewGuid();
            string[] events = ["{\"Timestamp\":\"1970-01-01T00:00:00Z\",\"MessageTemplate\":\"SECRET subtitle\",\"Exception\":\"SECRET key\",\"Properties\":{\"Kind\":\"StageFailed\",\"CorrelationId\":\"" + correlation + "\",\"Authorization\":\"SECRET\",\"Url\":\"https://SECRET/\",\"Stage\":\"Translation\"}}", "SECRET malformed"];
            var preview = SupportBundlePreview.Create(metrics, events, new Version(1, 2, 3), 4);
            stages.Clear();
            events[0] = "changed SECRET";
            Assert(preview.Entries.Count == 4 && !string.Join("", preview.Entries.Values).Contains("SECRET", StringComparison.Ordinal), "Bundle admitted private content.");
            Assert(preview.Entries["events.jsonl"].Contains(correlation.ToString(), StringComparison.Ordinal) &&
                preview.Entries["metrics.json"].Contains("P95Milliseconds", StringComparison.Ordinal), "Preview was not frozen or lost correlation.");
            var destination = Path.Combine(directory, "support.zip");
            var refused = false;
            try
            { await preview.ExportAsync(destination, false, null, CancellationToken.None); }
            catch (InvalidOperationException) { refused = true; }
            Assert(refused && !File.Exists(destination), "Export without consent was not refused before I/O.");
            await preview.ExportAsync(destination, true, null, CancellationToken.None);
            using (var archive = ZipFile.OpenRead(destination))
            {
                Assert(archive.Entries.Count == preview.Entries.Count, "Archive manifest mismatch.");
                foreach (var entry in archive.Entries)
                {
                    using var reader = new StreamReader(entry.Open());
                    Assert(await reader.ReadToEndAsync() == preview.Entries[entry.FullName], "Archive differs from reviewed preview.");
                }
                using var manifest = JsonDocument.Parse(preview.Entries["manifest.json"]);
                var names = manifest.RootElement.GetProperty("Entries").EnumerateArray().Select(value => value.GetString()).ToArray();
                Assert(names.Length == 4 && names.Distinct(StringComparer.Ordinal).Count() == 4 &&
                    archive.Entries.All(entry => names.Contains(entry.FullName, StringComparer.Ordinal)),
                    "Manifest names differ from actual archive entries.");
                var payloads = manifest.RootElement.GetProperty("Payloads").EnumerateArray().ToArray();
                Assert(payloads.Length == 3 && payloads.Select(payload => payload.GetProperty("Name").GetString())
                    .Distinct(StringComparer.Ordinal).Count() == 3, "Manifest payloads are incomplete or duplicated.");
                foreach (var payload in payloads)
                {
                    var name = payload.GetProperty("Name").GetString()!;
                    Assert(name != "manifest.json", "Manifest must not hash itself.");
                    var entry = archive.GetEntry(name)!;
                    using var bytes = new MemoryStream();
                    await using (var stream = entry.Open())
                    { await stream.CopyToAsync(bytes); }
                    Assert(bytes.ToArray().SequenceEqual(Encoding.UTF8.GetBytes(preview.Entries[name])),
                        "Archive bytes differ from UTF-8 preview (including BOM/encoding).");
                    Assert(entry.Length == payload.GetProperty("Bytes").GetInt64() &&
                        Convert.ToHexString(SHA256.HashData(bytes.ToArray())) == payload.GetProperty("Sha256").GetString(),
                        "Manifest length/hash does not match actual archived bytes.");
                }
            }
            var original = await File.ReadAllBytesAsync(destination);
            try
            { await preview.ExportAsync(destination, true, null, CancellationToken.None); throw new InvalidOperationException("Existing archive overwritten."); }
            catch (IOException) { }
            Assert(original.SequenceEqual(await File.ReadAllBytesAsync(destination)), "Existing destination was changed.");
            using var cancelled = new CancellationTokenSource();
            var cancelPath = Path.Combine(directory, "cancelled.zip");
            try
            { await preview.ExportAsync(cancelPath, true, new CancelProgress(cancelled), cancelled.Token); throw new InvalidOperationException("Mid-export cancellation ignored."); }
            catch (OperationCanceledException) { }
            Assert(!File.Exists(cancelPath) && Directory.GetFiles(directory, "*.partial").Length == 0, "Cancelled/failed export left partial files.");
        }
        finally { Directory.Delete(directory, recursive: true); }
        Console.WriteLine("Support bundle: immutable preview, hostile event exclusion, exact archive entries/content, explicit consent, no overwrite and mid-write cleanup passed.");
    }

    private static void Assert(bool value, string message)
    { if (!value) { throw new InvalidOperationException(message); } }
    private sealed class CancelProgress(CancellationTokenSource source) : IProgress<string>
    {
        public void Report(string value) => source.Cancel();
    }
}
