using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace GameLingoAI.Infrastructure.Ocr;

public sealed class OcrModelProvisioner
{
    private const string IntegrityFileName = ".integrity.json";
    private static readonly string[] RequiredFileNames = ["inference.json", "inference.pdiparams", "inference.yml"];
    private static readonly JsonSerializerOptions IntegrityJsonOptions = new() { WriteIndented = true };
    private readonly HttpClient _httpClient;
    private readonly string _modelsRoot;

    public OcrModelProvisioner(HttpClient httpClient, string modelsRoot)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsRoot);
        _modelsRoot = Path.GetFullPath(modelsRoot);
    }

    public async Task<OcrModelStatus> GetStatusAsync(
        OcrModelPackage package,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        var installationPath = GetInstallationPath(package);
        if (!Directory.Exists(installationPath))
        {
            return HasPartialDownload(package)
                ? new OcrModelStatus(OcrModelState.Partial, "A partial model download can be resumed.")
                : new OcrModelStatus(OcrModelState.Missing, "The OCR model has not been downloaded.");
        }

        var integrityPath = Path.Combine(installationPath, IntegrityFileName);
        if (!File.Exists(integrityPath))
        {
            return new OcrModelStatus(OcrModelState.Corrupt, "The model integrity manifest is missing.", installationPath);
        }

        try
        {
            if (new FileInfo(integrityPath).Length > 2 * 1024 * 1024)
            { return new OcrModelStatus(OcrModelState.Corrupt, "The model integrity manifest is too large.", installationPath); }
            await using var stream = File.OpenRead(integrityPath);
            var manifest = await JsonSerializer.DeserializeAsync<IntegrityManifest>(stream, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            if (manifest is null || !string.Equals(manifest.PackageId, package.Id, StringComparison.Ordinal) ||
                manifest.Files is null || manifest.Files.Count is < 1 or > 4096)
            {
                return new OcrModelStatus(OcrModelState.Corrupt, "The model integrity manifest is invalid.", installationPath);
            }

            var verified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file is null || string.IsNullOrWhiteSpace(file.RelativePath) || file.SizeBytes < 0 ||
                    file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                { return new OcrModelStatus(OcrModelState.Corrupt, "The model integrity entry is invalid.", installationPath); }
                var path = ResolveContainedPath(installationPath, file.RelativePath);
                if (!verified.Add(path) || !File.Exists(path) || new FileInfo(path).Length != file.SizeBytes ||
                    !string.Equals(await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return new OcrModelStatus(OcrModelState.Corrupt, $"Model file '{file.RelativePath}' failed integrity validation.", installationPath);
                }
            }

            var required = new[] { package.DetectionDirectory, package.RecognitionDirectory }
                .SelectMany(directory => RequiredFileNames
                    .Select(name => ResolveContainedPath(installationPath, Path.Combine(directory, name))));
            if (!HasRequiredModelFiles(package, installationPath) || required.Any(path => !verified.Contains(path)))
            {
                return new OcrModelStatus(OcrModelState.Corrupt, "Required PaddleOCR model files are missing.", installationPath);
            }

            return new OcrModelStatus(OcrModelState.Ready, "The model is installed and verified.", installationPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return new OcrModelStatus(OcrModelState.Corrupt, "The model integrity manifest could not be verified.", installationPath);
        }
    }

    /// <summary>Downloads a model only after the caller invokes this explicit provisioning operation.</summary>
    public async Task<OcrModelStatus> ProvisionAsync(
        OcrModelPackage package,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_modelsRoot);
        var downloadsPath = Path.Combine(_modelsRoot, ".downloads", package.Id);
        Directory.CreateDirectory(downloadsPath);
        long completedBytes = 0;

        foreach (var artifact in package.Artifacts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var partialPath = Path.Combine(downloadsPath, artifact.FileName + ".partial");
            await DownloadArtifactAsync(
                artifact,
                partialPath,
                bytes => progress?.Report((completedBytes + bytes) / (double)package.DownloadSizeBytes),
                cancellationToken).ConfigureAwait(false);
            completedBytes += artifact.SizeBytes;
        }

        var stagingPath = Path.Combine(_modelsRoot, $".staging-{package.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingPath);
        try
        {
            foreach (var artifact in package.Artifacts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var partialPath = Path.Combine(downloadsPath, artifact.FileName + ".partial");
                await ExtractTarAsync(partialPath, stagingPath, cancellationToken).ConfigureAwait(false);
            }

            if (!HasRequiredModelFiles(package, stagingPath))
            {
                throw new InvalidDataException("Downloaded archives do not contain the required PaddleOCR model files.");
            }

            var integrity = await CreateIntegrityManifestAsync(package.Id, stagingPath, cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(
                Path.Combine(stagingPath, IntegrityFileName),
                JsonSerializer.Serialize(integrity, IntegrityJsonOptions),
                cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            InstallStagingDirectory(package, stagingPath);
            if (Directory.Exists(downloadsPath))
            {
                Directory.Delete(downloadsPath, recursive: true);
            }

            progress?.Report(1);
        }
        catch
        {
            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            throw;
        }

        return await GetStatusAsync(package, cancellationToken).ConfigureAwait(false);
    }

    public string GetInstallationPath(OcrModelPackage package) =>
        ResolveContainedPath(_modelsRoot, package.Id);

    private async Task DownloadArtifactAsync(
        OcrModelArtifact artifact,
        string partialPath,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength == artifact.SizeBytes)
        {
            if (string.Equals(await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                reportBytes(existingLength);
                return;
            }
            File.Delete(partialPath);
            existingLength = 0;
        }
        if (existingLength > artifact.SizeBytes)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, artifact.DownloadUri);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (response.StatusCode == HttpStatusCode.PartialContent &&
            (response.Content.Headers.ContentRange is not { } range || !range.HasRange || !range.HasLength ||
             range.Unit != "bytes" || range.From != existingLength || range.Length != artifact.SizeBytes || range.To != artifact.SizeBytes - 1))
        { throw new InvalidDataException("Model server returned an incompatible resume range; no bytes were appended."); }
        if (!append)
        {
            existingLength = 0;
        }

        await using (var destination = new FileStream(
                         partialPath,
                         append ? FileMode.Append : FileMode.Create,
                         FileAccess.Write,
                         FileShare.None,
                         81920,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        {
            var buffer = new byte[81920];
            var total = existingLength;
            reportBytes(total);
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > artifact.SizeBytes)
                {
                    throw new InvalidDataException("Model download exceeded its declared size.");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                reportBytes(total);
            }
        }

        if (new FileInfo(partialPath).Length != artifact.SizeBytes ||
            !string.Equals(await ComputeSha256Async(partialPath, cancellationToken).ConfigureAwait(false), artifact.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partialPath);
            throw new InvalidDataException("Downloaded model failed size or SHA-256 validation; retry will start cleanly.");
        }
    }

    private static async Task ExtractTarAsync(string archivePath, string destinationRoot, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new TarReader(stream, leaveOpen: false);
        while (reader.GetNextEntry() is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = ResolveContainedPath(destinationRoot, entry.Name.Replace('/', Path.DirectorySeparatorChar));
            switch (entry.EntryType)
            {
                case TarEntryType.Directory:
                    Directory.CreateDirectory(path);
                    break;
                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        if (entry.DataStream is null)
                        {
                            throw new InvalidDataException("A model archive file entry has no data.");
                        }

                        await entry.DataStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new InvalidDataException($"Unsupported entry type '{entry.EntryType}' in model archive.");
            }
        }
    }

    private void InstallStagingDirectory(OcrModelPackage package, string stagingPath)
    {
        var installationPath = GetInstallationPath(package);
        var backupPath = installationPath + $".backup-{Guid.NewGuid():N}";
        if (Directory.Exists(installationPath))
        {
            Directory.Move(installationPath, backupPath);
        }

        try
        {
            Directory.Move(stagingPath, installationPath);
            if (Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }
        }
        catch
        {
            if (!Directory.Exists(installationPath) && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, installationPath);
            }

            throw;
        }
    }

    private static async Task<IntegrityManifest> CreateIntegrityManifestAsync(
        string packageId,
        string root,
        CancellationToken cancellationToken)
    {
        var files = new List<IntegrityFile>();
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            files.Add(new IntegrityFile(
                relative,
                new FileInfo(path).Length,
                await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false)));
        }

        return new IntegrityManifest(packageId, files);
    }

    private static bool HasRequiredModelFiles(OcrModelPackage package, string root)
    {
        foreach (var directory in new[] { package.DetectionDirectory, package.RecognitionDirectory })
        {
            var modelPath = ResolveContainedPath(root, directory);
            if (!File.Exists(Path.Combine(modelPath, "inference.json")) ||
                !File.Exists(Path.Combine(modelPath, "inference.pdiparams")) ||
                !File.Exists(Path.Combine(modelPath, "inference.yml")))
            {
                return false;
            }
        }

        return true;
    }

    private bool HasPartialDownload(OcrModelPackage package)
    {
        var path = Path.Combine(_modelsRoot, ".downloads", package.Id);
        return Directory.Exists(path) && Directory.EnumerateFiles(path, "*.partial").Any();
    }

    private static string ResolveContainedPath(string root, string relativePath)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relativePath));
        if (!fullPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A model path escapes its allowed directory.");
        }

        return fullPath;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private sealed record IntegrityManifest(string PackageId, IReadOnlyList<IntegrityFile> Files);

    private sealed record IntegrityFile(string RelativePath, long SizeBytes, string Sha256);
}
