using System.Security.Cryptography;
using GameLingoAI.Core.Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR;
using Sdcb.PaddleOCR.Models;
using YamlDotNet.RepresentationModel;

namespace GameLingoAI.Infrastructure.Ocr;

public sealed class SdcbPaddleOcrRuntimeFactory : IPaddleOcrRuntimeFactory
{
    private readonly int _cpuMathThreadCount;
    private readonly bool _reusePredictors;

    public SdcbPaddleOcrRuntimeFactory() : this(Math.Clamp(Environment.ProcessorCount / 2, 1, 8)) { }

    /// <summary>Bounded native CPU parallelism; construction alone does not load native libraries.</summary>
    public SdcbPaddleOcrRuntimeFactory(int cpuMathThreadCount, bool reusePredictors = true)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cpuMathThreadCount, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cpuMathThreadCount, 8);
        _cpuMathThreadCount = cpuMathThreadCount;
        _reusePredictors = reusePredictors;
    }

    public IPaddleOcrRuntime Create(OcrModelPackage package, string installationPath)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentException.ThrowIfNullOrWhiteSpace(installationPath);
        var detectionPath = Path.Combine(installationPath, package.DetectionDirectory);
        var recognitionPath = Path.Combine(installationPath, package.RecognitionDirectory);
        var model = new FullOcrModel(
            new DirectoryDetectionModel(detectionPath),
            new DirectoryRecognitionModel(recognitionPath));
        var configure = PaddleDevice.Mkldnn(
            cpuMathThreadCount: _cpuMathThreadCount,
            cacheCapacity: 1,
            memoryOptimized: true,
            glogEnabled: false);
        if (_reusePredictors)
        { return new ReusablePaddleOcrRuntime(model, configure); }
        var engine = new PaddleOcrAll(model, configure)
        {
            AllowRotateDetection = false,
            Enable180Classification = false
        };
        return new SdcbPaddleOcrRuntime(engine);
    }

    private sealed class DirectoryDetectionModel : DetectionModel
    {
        private readonly string _directory;

        public DirectoryDetectionModel(string directory)
            : base(ModelVersion.V5) => _directory = directory;

        public override PaddleConfig CreateConfig()
        {
            var config = PaddleConfig.FromModelDir(_directory);
            config.NewExecutorEnabled = true;
            return config;
        }
    }

    private sealed class DirectoryRecognitionModel : RecognizationModel
    {
        private readonly string _directory;
        private readonly IReadOnlyList<string> _labels;

        public DirectoryRecognitionModel(string directory)
            : base(ModelVersion.V5)
        {
            _directory = directory;
            _labels = LoadLabels(Path.Combine(directory, "inference.yml"));
        }

        public override PaddleConfig CreateConfig()
        {
            var config = PaddleConfig.FromModelDir(_directory);
            config.NewExecutorEnabled = true;
            return config;
        }

        public override string GetLabelByIndex(int i) => GetLabelByIndex(i, _labels);

        private static string[] LoadLabels(string yamlPath)
        {
            using var reader = File.OpenText(yamlPath);
            var yaml = new YamlStream();
            yaml.Load(reader);
            var root = (YamlMappingNode)yaml.Documents[0].RootNode;
            var postProcess = (YamlMappingNode)root.Children[new YamlScalarNode("PostProcess")];
            var values = (YamlSequenceNode)postProcess.Children[new YamlScalarNode("character_dict")];
            return values.Children.Cast<YamlScalarNode>().Select(node => node.Value ?? string.Empty).ToArray();
        }
    }
}

internal sealed class SdcbPaddleOcrRuntime : IPaddleOcrRuntime
{
    private PaddleOcrAll? _engine;

    public SdcbPaddleOcrRuntime(PaddleOcrAll engine) => _engine = engine;

    public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image)
    {
        var engine = _engine ?? throw new ObjectDisposedException(nameof(SdcbPaddleOcrRuntime));
        var bytes = image.Pixels.ToArray();
        try
        {
            var sourceType = image.PixelFormat == OcrPixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC4;
            using var source = Mat.FromPixelData(image.Height, image.Width, sourceType, bytes);
            using var bgr = new Mat();
            Cv2.CvtColor(
                source,
                bgr,
                image.PixelFormat == OcrPixelFormat.Gray8 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
            var result = engine.Run(bgr);
            return result.Regions.Select(region =>
            {
                var points = region.Rect.Points();
                var left = points.Min(point => point.X);
                var top = points.Min(point => point.Y);
                var right = points.Max(point => point.X);
                var bottom = points.Max(point => point.Y);
                return new PaddleRuntimeSpan(region.Text, region.Score, left, top, right - left, bottom - top);
            }).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public void Dispose() => Interlocked.Exchange(ref _engine, null)?.Dispose();
}
