using System.Runtime.Intrinsics.X86;

namespace GameLingoAI.Infrastructure.Ocr;

/// <summary>Presence-only preflight for the pinned Windows x64 native OCR assets. Never loads code or installs components.</summary>
public static class OcrRuntimePrerequisites
{
    private static readonly string[] VisualCppFiles = ["vcomp140.dll", "msvcp140.dll", "vcruntime140.dll", "vcruntime140_1.dll"];
    private static readonly string[] MediaFiles = ["mf.dll", "mfplat.dll", "mfreadwrite.dll"];

    public static IReadOnlyList<string> Check() => Evaluate(AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.System), Environment.Is64BitProcess, Avx.IsSupported, File.Exists);

    /// <summary>Replace filesystem/CPU observations for deterministic tests; file presence is not native inference certification.</summary>
    public static IReadOnlyList<string> Evaluate(string applicationDirectory, string systemDirectory,
        bool is64BitProcess, bool avxSupported, Func<string, bool> exists)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemDirectory);
        ArgumentNullException.ThrowIfNull(exists);
        var missing = new List<string>();
        if (!is64BitProcess)
        { missing.Add("Use the Windows x64 application package."); }
        if (!avxSupported)
        { missing.Add("Local OCR requires an AVX-capable CPU."); }
        if (VisualCppFiles.Any(file => !exists(Path.Combine(applicationDirectory, file)) && !exists(Path.Combine(systemDirectory, file))))
        { missing.Add("Install or repair Microsoft's Visual C++ 2015-2022 Redistributable (x64) before using OCR."); }
        if (MediaFiles.Any(file => !exists(Path.Combine(systemDirectory, file))))
        { missing.Add("Windows Media Foundation is missing; on Windows N, enable the official Media Feature Pack before using OCR."); }
        return missing.AsReadOnly();
    }
}
