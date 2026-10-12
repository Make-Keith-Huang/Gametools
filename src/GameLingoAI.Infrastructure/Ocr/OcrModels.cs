using System.Text.RegularExpressions;
using GameLingoAI.Core.Models;

namespace GameLingoAI.Infrastructure.Ocr;

public enum OcrModelArchiveKind
{
    Tar
}

public sealed record OcrModelArtifact(
    string FileName,
    Uri DownloadUri,
    long SizeBytes,
    string Sha256,
    OcrModelArchiveKind ArchiveKind);

public sealed record OcrModelPackage
{
    public OcrModelPackage(
        string id,
        string displayName,
        IReadOnlySet<OcrLanguage> languages,
        IReadOnlyList<OcrModelArtifact> artifacts,
        string licenseName,
        Uri licenseUri,
        Uri sourceUri,
        string detectionDirectory,
        string recognitionDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!Regex.IsMatch(id, "^[a-z0-9][a-z0-9.-]{0,63}$", RegexOptions.CultureInvariant))
        {
            throw new ArgumentException("Model package ID contains unsupported characters.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentNullException.ThrowIfNull(languages);
        ArgumentNullException.ThrowIfNull(artifacts);
        if (languages.Count == 0 || artifacts.Count == 0)
        {
            throw new ArgumentException("A model package must declare languages and artifacts.");
        }

        foreach (var artifact in artifacts)
        {
            ValidateArtifact(artifact);
        }

        Id = id;
        DisplayName = displayName;
        Languages = new HashSet<OcrLanguage>(languages);
        Artifacts = Array.AsReadOnly(artifacts.ToArray());
        LicenseName = licenseName;
        LicenseUri = ValidateHttps(licenseUri, nameof(licenseUri));
        SourceUri = ValidateHttps(sourceUri, nameof(sourceUri));
        DetectionDirectory = ValidateRelativeDirectory(detectionDirectory, nameof(detectionDirectory));
        RecognitionDirectory = ValidateRelativeDirectory(recognitionDirectory, nameof(recognitionDirectory));
    }

    public string Id { get; }

    public string DisplayName { get; }

    public IReadOnlySet<OcrLanguage> Languages { get; }

    public IReadOnlyList<OcrModelArtifact> Artifacts { get; }

    public string LicenseName { get; }

    public Uri LicenseUri { get; }

    public Uri SourceUri { get; }

    public string DetectionDirectory { get; }

    public string RecognitionDirectory { get; }

    public long DownloadSizeBytes => Artifacts.Sum(artifact => artifact.SizeBytes);

    private static void ValidateArtifact(OcrModelArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (Path.GetFileName(artifact.FileName) != artifact.FileName || string.IsNullOrWhiteSpace(artifact.FileName))
        {
            throw new ArgumentException("Artifact file name must be a plain file name.", nameof(artifact));
        }

        ValidateHttps(artifact.DownloadUri, nameof(artifact));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(artifact.SizeBytes);
        if (artifact.SizeBytes > 512L * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(artifact), "Model artifacts are capped at 512 MiB.");
        }

        if (artifact.Sha256.Length != 64 || !artifact.Sha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Artifact SHA-256 must contain 64 hexadecimal characters.", nameof(artifact));
        }
    }

    private static Uri ValidateHttps(Uri uri, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(uri, parameterName);
        if (!uri.IsAbsoluteUri || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Model metadata URLs must use HTTPS.", parameterName);
        }

        return uri;
    }

    private static string ValidateRelativeDirectory(string path, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path, parameterName);
        if (Path.IsPathRooted(path) || path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".."))
        {
            throw new ArgumentException("Model directory must be a safe relative path.", parameterName);
        }

        return path;
    }
}

public enum OcrModelState
{
    Missing,
    Partial,
    Ready,
    Corrupt
}

public sealed record OcrModelStatus(OcrModelState State, string Message, string? InstallationPath = null);

public static class PaddleOcrModelCatalog
{
    public static OcrModelPackage PpOcrV5Mobile { get; } = new(
        "paddleocr-v5-mobile",
        "PaddleOCR v5 mobile (CPU)",
        new HashSet<OcrLanguage>
        {
            OcrLanguage.English,
            OcrLanguage.SimplifiedChinese,
            OcrLanguage.TraditionalChinese,
            OcrLanguage.Japanese
        },
        new[]
        {
            new OcrModelArtifact(
                "PP-OCRv5_mobile_det_infer.tar",
                new Uri("https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-OCRv5_mobile_det_infer.tar"),
                4_935_680,
                "50446E5D01AC2A73D5319C89513281F6578414C888C602F9AF13F93FEEFFFC58",
                OcrModelArchiveKind.Tar),
            new OcrModelArtifact(
                "PP-OCRv5_mobile_rec_infer.tar",
                new Uri("https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0/PP-OCRv5_mobile_rec_infer.tar"),
                16_834_560,
                "566B9512B34E34A9F0DB54D87B51FA5A0B9ED2CF1AB7E49728CC0B8B5A64F414",
                OcrModelArchiveKind.Tar)
        },
        "Apache-2.0",
        new Uri("https://github.com/PaddlePaddle/PaddleOCR/blob/main/LICENSE"),
        new Uri("https://github.com/PaddlePaddle/PaddleOCR"),
        "PP-OCRv5_mobile_det_infer",
        "PP-OCRv5_mobile_rec_infer");

    public static IReadOnlyList<OcrModelPackage> All { get; } = new[] { PpOcrV5Mobile };
}
