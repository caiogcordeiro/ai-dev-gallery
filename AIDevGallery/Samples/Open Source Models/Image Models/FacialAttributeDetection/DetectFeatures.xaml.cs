// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AIDevGallery.Models;
using AIDevGallery.Samples.Attributes;
using AIDevGallery.Samples.SharedCode;
using AIDevGallery.Utils;
using CommunityToolkit.WinUI.Controls;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media;

namespace AIDevGallery.Samples.OpenSourceModels.FacialAttributeDetection;

[GallerySample(
    Model1Types = [ModelType.FacialAttributeDetection],
    Scenario = ScenarioType.ImageDetectFeatures,
    SharedCode = [
        SharedCodeEnum.Prediction,
        SharedCodeEnum.BitmapFunctions,
        SharedCodeEnum.DeviceUtils,
        SharedCodeEnum.FaceHelpers
    ],
    NugetPackageReferences = [
        "System.Drawing.Common",
        "Microsoft.ML.OnnxRuntime.DirectML",
        "Microsoft.ML.OnnxRuntime.Extensions",
        "CommunityToolkit.WinUI.Helpers",
        "CommunityToolkit.WinUI.Controls.CameraPreview",
        "Microsoft.Graphics.Win2D"
    ],
    Name = "Face Detection",
    Id = "9b74ccc0-f5f7-417f-bed0-712ffc063508",
    Icon = "\uE8B3")]

internal sealed partial class DetectFeatures : BaseSamplePage
{
    private InferenceSession? _inferenceSession;
    private Dictionary<string, bool>? predictions;

    private DispatcherTimer _frameRateTimer;
    private VideoFrame? _latestVideoFrame;

    private bool modelActive = true;

    private DateTimeOffset lastFaceDetectionCount = DateTimeOffset.Now;
    private int faceDetectionsCount;
    private int faceDetectionsPerSecond;

    private int originalImageWidth = 1280;
    private int originalImageHeight = 720;

    public DetectFeatures()
    {
        this.Unloaded += FaceDetectionUnloaded;
        this.InitializeComponent();
        InitializeCameraPreviewControl();

        _frameRateTimer = new DispatcherTimer();
        InitializeFrameRateTimer();
    }

    private void InitializeFrameRateTimer()
    {
        _frameRateTimer.Interval = TimeSpan.FromMilliseconds(33);
        _frameRateTimer.Tick += FrameRateTimer_Tick;
        _frameRateTimer.Start();
    }

    private void FrameRateTimer_Tick(object? sender, object e)
    {
        if (_latestVideoFrame != null)
        {
            ProcessFrame(_latestVideoFrame);
            _latestVideoFrame = null;
        }
    }

    private async void FaceDetectionUnloaded(object sender, RoutedEventArgs e)
    {
        lock (this)
        {
            _inferenceSession?.Dispose();
            _inferenceSession = null;
            _latestVideoFrame?.Dispose();

            CameraPreviewControl.CameraHelper.FrameArrived -= CameraPreviewControl_FrameArrived!;
            CameraPreviewControl.PreviewFailed -= CameraPreviewControl_PreviewFailed!;
            CameraPreviewControl.Stop();
        }

        await CameraPreviewControl.CameraHelper.CleanUpAsync();
    }

    protected override async Task LoadModelAsync(SampleNavigationParameters sampleParams)
    {
        await InitModel(sampleParams.ModelPath, sampleParams.HardwareAccelerator);
        sampleParams.NotifyCompletion();

        InitializeCameraPreviewControl();
    }

    private Task InitModel(string modelPath, HardwareAccelerator hardwareAccelerator)
    {
        return Task.Run(() =>
        {
            if (_inferenceSession != null)
            {
                return;
            }

            SessionOptions sessionOptions = new();
            sessionOptions.RegisterOrtExtensions();
            if (hardwareAccelerator == HardwareAccelerator.DML)
            {
                sessionOptions.AppendExecutionProvider_DML(DeviceUtils.GetBestDeviceId());
            }
            else if (hardwareAccelerator == HardwareAccelerator.QNN)
            {
                Dictionary<string, string> options = new()
                {
                    { "backend_path", "QnnHtp.dll" },
                    { "htp_performance_mode", "high_performance" },
                    { "htp_graph_finalization_optimization_mode", "3" }
                };
                sessionOptions.AppendExecutionProvider("QNN", options);
            }

            _inferenceSession = new InferenceSession(modelPath, sessionOptions);
        });
    }

    private async void InitializeCameraPreviewControl()
    {
        var cameraHelper = CameraPreviewControl.CameraHelper;

        CameraPreviewControl.PreviewFailed += CameraPreviewControl_PreviewFailed!;
        await CameraPreviewControl.StartAsync(cameraHelper!);
        CameraPreviewControl.CameraHelper.FrameArrived += CameraPreviewControl_FrameArrived!;
    }

    private readonly SemaphoreSlim _frameProcessingLock = new SemaphoreSlim(1);

    private void CameraPreviewControl_FrameArrived(object sender, FrameEventArgs e)
    {
        _latestVideoFrame = e.VideoFrame;
    }

    private void CameraPreviewControl_PreviewFailed(object sender, PreviewFailedEventArgs e)
    {
        var errorMessage = e.Error;
    }

    private void ToggleModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton clickedButton)
        {
            if (modelActive)
            {
                FaceDetText.Text = "Start feature detection";
            }
            else
            {
                FaceDetText.Text = "Stop feature detection";
            }

            lock (this)
            {
                predictions?.Clear();
                faceDetectionsCount = 0;
                faceDetectionsPerSecond = 0;
            }

            modelActive = !modelActive;
            canvasAnimatedControl.Invalidate(); // Force redraw
        }
    }

    private async void ProcessFrame(VideoFrame videoFrame)
    {
        var softwareBitmap = videoFrame.SoftwareBitmap;
        try
        {
            if (!_frameProcessingLock.Wait(0))
            {
                return;
            }

            if (modelActive)
            {
                await DetectFace(videoFrame);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error: {ex.Message}");
        }
        finally
        {
            _frameProcessingLock.Release();
        }
    }

    private async Task DetectFace(VideoFrame videoFrame)
    {
        if (_inferenceSession == null || videoFrame == null)
        {
            return;
        }

        originalImageWidth = videoFrame.SoftwareBitmap.PixelWidth;
        originalImageHeight = videoFrame.SoftwareBitmap.PixelHeight;

        var inputMetadataName = _inferenceSession.InputNames[0];
        var inputDimensions = _inferenceSession.InputMetadata[inputMetadataName].Dimensions;

        int modelInputHeight = inputDimensions[2];
        int modelInputWidth = inputDimensions[3];

        using Bitmap resizedImage = await BitmapFunctions.ResizeVideoFrameWithPadding(videoFrame, modelInputWidth, modelInputHeight);

        Tensor<float> input = new DenseTensor<float>([.. inputDimensions]);
        input = BitmapFunctions.PreprocessBitmapWithStdDev(resizedImage, input);

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(inputMetadataName, input)
        };

        try
        {
            lock (this)
            {
                if (_inferenceSession == null)
                {
                    return;
                }

                using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _inferenceSession.Run(inputs);
                {
                    predictions = Postprocess(results);
                }
            }
        }
        catch
        {
            lock (this)
            {
                predictions?.Clear();
            }
        }

        canvasAnimatedControl.Invalidate();
    }

    private Dictionary<string, bool> Postprocess(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results)
    {
        Dictionary<string, bool> attributes = new();

        foreach (var result in results)
        {
            var name = result.Name;
            if (name == "id_feature") continue; // skip embedding

            var tensor = result.AsTensor<float>();
            if (tensor.Dimensions.Length == 2 && tensor.Dimensions[0] == 1 && tensor.Dimensions[1] == 2)
            {
                // Binary classification: pick index 1 as "true"
                bool isTrue = tensor[0, 1] > tensor[0, 0];
                attributes[name] = isTrue;
            }
            else
            {
                // Ignore anything not 1x2 (like liveness_feature or unexpected shape)
                continue;
            }
        }

        return attributes;
    }


    private DateTimeOffset lastRenderTime = DateTimeOffset.Now;
    private int framesRenderedSinceLastSecond;
    private int fps;

    private void CanvasControl_Draw(ICanvasAnimatedControl sender, CanvasAnimatedDrawEventArgs args)
    {
        args.DrawingSession.Clear(Colors.Transparent);

        if (predictions?.Count > 0 && modelActive)
        {
            DrawPredictions(args.DrawingSession);
            UpdateFaceDetectionsPerSecond();
        }

        DrawFPS(args.DrawingSession);
    }

    private void DrawPredictions(CanvasDrawingSession drawingSession)
    {
        if (predictions == null || predictions.Count == 0)
        {
            return;
        }

        float x = 10f;
        float y = 60f;

        foreach (var kvp in predictions)
        {
            string keyText = $"{kvp.Key}: ";
            string valueText = kvp.Value ? "True" : "False";

            drawingSession.DrawText(keyText, x, y, Colors.Black);

            using CanvasTextFormat tf = new CanvasTextFormat();
            using var keyTextLayout = new CanvasTextLayout(drawingSession.Device, keyText, tf, float.MaxValue, float.MaxValue);

            drawingSession.DrawText(valueText, x + (float)keyTextLayout.LayoutBounds.Width + 10, y, kvp.Value ? Colors.LimeGreen : Colors.Red);

            y += 20f;
        }
    }

    private void UpdateFaceDetectionsPerSecond()
    {
        var currentTime = DateTimeOffset.Now;
        faceDetectionsCount++;

        if (currentTime - lastFaceDetectionCount > TimeSpan.FromSeconds(1))
        {
            lastFaceDetectionCount = currentTime;
            faceDetectionsPerSecond = faceDetectionsCount;
            faceDetectionsCount = 0;
        }
    }

    private void DrawFPS(CanvasDrawingSession drawingSession)
    {
        var currentTime = DateTimeOffset.Now;
        framesRenderedSinceLastSecond++;

        if (currentTime - lastRenderTime > TimeSpan.FromSeconds(1))
        {
            lastRenderTime = currentTime;
            fps = framesRenderedSinceLastSecond;
            framesRenderedSinceLastSecond = 0;
        }

        drawingSession.DrawText($"FPS: {fps}", 10, 10, Colors.Blue);
        drawingSession.DrawText($"Face detections per second: {faceDetectionsPerSecond}", 10, 30, Colors.Blue);
    }

    private void CameraPreviewControl_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (originalImageWidth == 0 || originalImageHeight == 0)
        {
            return;
        }

        UpdateSize();
    }

    private void UpdateSize()
    {
        var ratio = originalImageWidth / (float)originalImageHeight;
        if (CameraPreviewControl.ActualWidth / CameraPreviewControl.ActualHeight > ratio)
        {
            canvasAnimatedControl.Width = CameraPreviewControl.ActualHeight * ratio;
            canvasAnimatedControl.Height = CameraPreviewControl.ActualHeight;
        }
        else
        {
            canvasAnimatedControl.Width = CameraPreviewControl.ActualWidth;
            canvasAnimatedControl.Height = CameraPreviewControl.ActualWidth / ratio;
        }
    }
}