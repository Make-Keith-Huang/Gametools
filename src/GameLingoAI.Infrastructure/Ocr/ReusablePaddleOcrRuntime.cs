using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using GameLingoAI.Core.Models;
using OpenCvSharp;
using Sdcb.PaddleInference;
using Sdcb.PaddleOCR.Models;

namespace GameLingoAI.Infrastructure.Ocr;

// Adapted preprocessing, geometry and CTC conventions from PaddleSharp commit
// 139bc184a6d86c0c60bb8b8a90fb641b21c0b0e6 (Apache-2.0).
// License: packaging/licenses/paddle/paddlesharp-a875839-LICENSE.txt.
// Unlike upstream Run, predictors are owned once and used serially, never cloned per call.
internal sealed class ReusablePaddleOcrRuntime : IPaddleOcrRuntime
{
    private readonly object _gate = new();
    private readonly PaddlePredictor _detector;
    private readonly PaddlePredictor _recognizer;
    private readonly RecognizationModel _model;
    private bool _disposed;

    internal ReusablePaddleOcrRuntime(FullOcrModel model, Action<PaddleConfig> configure)
    {
        _model = model.RecognizationModel;
        using var detectionConfig = model.DetectionModel.CreateConfig();
        model.DetectionModel.ConfigureDevice(detectionConfig, configure);
        _detector = detectionConfig.CreatePredictor();
        try
        {
            using var recognitionConfig = _model.CreateConfig();
            _model.ConfigureDevice(recognitionConfig, configure);
            _recognizer = recognitionConfig.CreatePredictor();
        }
        catch { _detector.Dispose(); throw; }
    }

    public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image) => Recognize(image, CancellationToken.None);

    public IReadOnlyList<PaddleRuntimeSpan> Recognize(PreprocessedRegionImage image, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(image);
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            token.ThrowIfCancellationRequested();
            var pixels = image.Pixels.ToArray();
            try
            {
                using var source = Mat.FromPixelData(image.Height, image.Width,
                    image.PixelFormat == OcrPixelFormat.Gray8 ? MatType.CV_8UC1 : MatType.CV_8UC4, pixels);
                using var bgr = new Mat();
                Cv2.CvtColor(source, bgr, image.PixelFormat == OcrPixelFormat.Gray8 ? ColorConversionCodes.GRAY2BGR : ColorConversionCodes.BGRA2BGR);
                return RecognizeBoxes(bgr, Detect(bgr, token), token);
            }
            finally { CryptographicOperations.ZeroMemory(pixels); }
        }
    }

    private RotatedRect[] Detect(Mat image, CancellationToken token)
    {
        var ratio = 960.0 / Math.Max(image.Width, image.Height);
        using var resized = ratio < 1 ? image.Resize(default, ratio, ratio) : image.Clone();
        using var padded = resized.CopyMakeBorder(0, (32 - resized.Height % 32) % 32,
            0, (32 - resized.Width % 32) % 32, BorderTypes.Constant, Scalar.Black);
        var output = Infer(_detector, [1, 3, padded.Height, padded.Width], Normalize(padded, true), token);
        if (output.Shape.Length != 4 || output.Shape[0] != 1 || output.Shape[1] != 1 ||
            output.Shape[2] < resized.Height || output.Shape[3] < resized.Width ||
            output.Data.Length != checked(output.Shape[2] * output.Shape[3]))
        { throw new InvalidDataException("Unexpected detection tensor."); }
        using var prediction = Mat.FromPixelData(output.Shape[2], output.Shape[3], MatType.CV_32FC1, output.Data);
        using var roi = prediction[0, resized.Height, 0, resized.Width];
        using var bytes = new Mat();
        roi.ConvertTo(bytes, MatType.CV_8UC1, 255);
        using var binary = bytes.Threshold((int)(0.3f * 255), 255, ThresholdTypes.Binary);
        var boxes = new List<RotatedRect>();
        var scale = (float)((double)image.Width / resized.Width);
        foreach (var contour in binary.FindContoursAsArray(RetrievalModes.List, ContourApproximationModes.ApproxSimple))
        {
            token.ThrowIfCancellationRequested();
            var x1 = Math.Clamp(contour.Min(p => p.X), 0, prediction.Width - 1);
            var x2 = Math.Clamp(contour.Max(p => p.X), 0, prediction.Width - 1);
            var y1 = Math.Clamp(contour.Min(p => p.Y), 0, prediction.Height - 1);
            var y2 = Math.Clamp(contour.Max(p => p.Y), 0, prediction.Height - 1);
            using var mask = new Mat(y2 - y1 + 1, x2 - x1 + 1, MatType.CV_8UC1, Scalar.Black);
            mask.FillPoly([contour.Select(p => new Point(p.X - x1, p.Y - y1)).ToArray()], new Scalar(1));
            using var region = prediction[y1, y2 + 1, x1, x2 + 1];
            if ((float)region.Mean(mask).Val0 <= 0.7f)
            { continue; }
            var perimeter = (float)Cv2.ArcLength(contour, true);
            if (perimeter < 1e-6f)
            { continue; }
            var rect = Cv2.MinAreaRect(contour);
            var expansion = 1.5f * (float)Math.Abs(Cv2.ContourArea(contour)) / perimeter;
            var size = new Size2f((rect.Size.Width + 2 * expansion) * scale, (rect.Size.Height + 2 * expansion) * scale);
            if (size.Width > 3 && size.Height > 3)
            { boxes.Add(new RotatedRect(rect.Center * scale, size, rect.Angle)); }
        }
        return boxes.OrderBy(box => box.Center.Y).ThenBy(box => box.Center.X).ToArray();
    }

    private PaddleRuntimeSpan[] RecognizeBoxes(Mat image, RotatedRect[] boxes, CancellationToken token)
    {
        var crops = new List<(int Index, Mat Image)>();
        try
        {
            for (var index = 0; index < boxes.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var rect = boxes[index].BoundingRect();
                rect = Rect.FromLTRB(Math.Clamp(rect.Left, 0, image.Width), Math.Clamp(rect.Top, 0, image.Height),
                    Math.Clamp(rect.Right, 0, image.Width), Math.Clamp(rect.Bottom, 0, image.Height));
                if (rect.Width <= 0 || rect.Height <= 0)
                { throw new InvalidDataException("Empty detection crop."); }
                crops.Add((index, image[rect]));
            }
            var result = new PaddleRuntimeSpan[boxes.Length];
            foreach (var batch in crops.OrderBy(crop => crop.Image.Width).Chunk(Math.Min(8, Environment.ProcessorCount)))
            {
                token.ThrowIfCancellationRequested();
                var height = _model.Shape.Height;
                var width = (int)Math.Ceiling(batch.Max(crop => (double)crop.Image.Width / crop.Image.Height * height));
                var stride = checked(3 * height * width);
                var input = new float[checked(batch.Length * stride)];
                try
                {
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var crop = batch[index].Image;
                        var resizedWidth = (int)Math.Ceiling(height * (1.0f * crop.Width / crop.Height));
                        using var resized = crop.Resize(new Size(resizedWidth, height));
                        using var padded = resized.CopyMakeBorder(0, 0, 0, width - resizedWidth, BorderTypes.Constant, Scalar.Gray);
                        var normalized = Normalize(padded, false);
                        try
                        { normalized.CopyTo(input, index * stride); }
                        finally { Array.Clear(normalized); }
                    }
                    var output = Infer(_recognizer, [batch.Length, 3, height, width], input, token);
                    if (output.Shape.Length != 3 || output.Shape[0] != batch.Length || output.Shape[1] <= 0 || output.Shape[2] <= 0 ||
                        output.Data.Length != checked(batch.Length * output.Shape[1] * output.Shape[2]))
                    { throw new InvalidDataException("Unexpected recognition tensor."); }
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var text = new StringBuilder();
                        var previous = -1;
                        var score = 0f;
                        for (var step = 0; step < output.Shape[1]; step++)
                        {
                            var row = output.Data.AsSpan((index * output.Shape[1] + step) * output.Shape[2], output.Shape[2]);
                            var best = 0;
                            for (var candidate = 0; candidate < row.Length; candidate++)
                            {
                                if (!float.IsFinite(row[candidate]))
                                { throw new InvalidDataException("Nonfinite recognition output."); }
                                if (row[candidate] > row[best])
                                { best = candidate; }
                            }
                            if (best != 0 && best != previous)
                            { text.Append(_model.GetLabelByIndex(best)); score += row[best]; }
                            previous = best;
                        }
                        var original = batch[index].Index;
                        var points = boxes[original].Points();
                        var left = points.Min(p => p.X);
                        var top = points.Min(p => p.Y);
                        result[original] = new PaddleRuntimeSpan(text.ToString(), score / text.Length, left, top,
                            points.Max(p => p.X) - left, points.Max(p => p.Y) - top);
                    }
                }
                finally { Array.Clear(input); }
            }
            return result;
        }
        finally
        {
            foreach (var crop in crops)
            { crop.Image.Dispose(); }
        }
    }

    private static float[] Normalize(Mat image, bool detection)
    {
        using var normalized = new Mat();
        image.ConvertTo(normalized, MatType.CV_32FC3, 1.0 / 255);
        var channels = normalized.Split();
        try
        {
            var count = checked(image.Height * image.Width);
            var data = new float[checked(3 * count)];
            for (var channel = 0; channel < 3; channel++)
            {
                var scale = detection ? channel switch { 0 => 1 / 0.229f, 1 => 1 / 0.224f, _ => 1 / 0.225f } : 2f;
                var mean = detection ? channel switch { 0 => 0.485f, 1 => 0.456f, _ => 0.406f } : 0.5f;
                channels[channel].ConvertTo(channels[channel], MatType.CV_32FC1, 1.0 * scale, (0.0 - mean) * scale);
                Marshal.Copy(channels[channel].Data, data, channel * count, count);
            }
            return data;
        }
        finally
        {
            foreach (var channel in channels)
            { channel.Dispose(); }
        }
    }

    private static (int[] Shape, float[] Data) Infer(PaddlePredictor predictor, int[] shape, float[] data, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            using (var input = predictor.GetInputTensor(predictor.InputNames[0]))
            { input.Shape = shape; input.SetData(data); }
            if (!predictor.Run())
            { throw new InvalidOperationException("Local OCR inference failed."); }
            token.ThrowIfCancellationRequested();
            using var output = predictor.GetOutputTensor(predictor.OutputNames[0]);
            return (output.Shape, output.GetData<float>());
        }
        finally { Array.Clear(data); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            { return; }
            _disposed = true;
            try
            { _recognizer.Dispose(); }
            finally { _detector.Dispose(); }
        }
    }
}
