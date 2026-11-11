using System;
using System.Threading;
using UnityEngine;

using Microsoft.Azure.Kinect.Sensor;

public class KinectManager : MonoBehaviour
{
    public static KinectManager Instance { get; private set; }

    // The sensor captures landscape frames; the mirror displays them rotated
    // into portrait. ColorWidth/ColorHeight describe the portrait image that
    // every consumer sees.
    private const int SensorColorWidth = 1280;
    private const int SensorColorHeight = 720;
    private const int ColorBytesPerPixel = 4;

    public const int ColorWidth = SensorColorHeight;
    public const int ColorHeight = SensorColorWidth;

    private const int BytesPerDepthPixel = 2;
    private const int DepthSampleWindowRadius = 2;

    public enum PortraitRotation
    {
        Clockwise90,
        CounterClockwise90
    }

    [Header("Portrait Camera")]
    [SerializeField]
    [Tooltip("Rotation applied to the captured image. Stop Play mode before changing this setting.")]
    private PortraitRotation portraitRotation = PortraitRotation.Clockwise90;

    [Header("Depth")]
    public bool enableDepth = true;

    private bool rotateClockwise;

    public Device Device { get; private set; }

    public float ColorFx { get; private set; }
    public float ColorFy { get; private set; }
    public float ColorCx { get; private set; }
    public float ColorCy { get; private set; }

    private Calibration calibration;

    private Thread cameraThread;

    private volatile bool cameraThreadRunning = false;
    private volatile bool shuttingDown = false;

    private readonly object errorLock = new object();

    private string pendingWorkerError = null;

    private readonly object colorLock = new object();

    private byte[] latestColorData = null;
    private byte[] displayedColorData = null;

    private bool newColorFrameAvailable = false;

    private readonly object depthLock = new object();

    private byte[] latestDepthData = null;

    private int publishedDepthWidth = 0;
    private int publishedDepthHeight = 0;

    private readonly ushort[] depthSampleWindow =
        new ushort[(DepthSampleWindowRadius * 2 + 1) * (DepthSampleWindowRadius * 2 + 1)];

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        InitializeKinect();
    }

    public bool TryGetColorFrame(out byte[] colorData)
    {
        lock (colorLock)
        {
            if (!newColorFrameAvailable || latestColorData == null)
            {
                colorData = null;
                return false;
            }

            byte[] reusableBuffer = displayedColorData;

            displayedColorData = latestColorData;
            latestColorData = reusableBuffer;

            colorData = displayedColorData;
            newColorFrameAvailable = false;

            return true;
        }
    }

    // Samples the registered depth image at a portrait color pixel. The depth
    // frame stays in sensor orientation and is mapped here, so no full-frame
    // depth rotation is needed.
    public bool TryGetDepthAtColorPixel(int x, int y, out float depthMeters)
    {
        depthMeters = 0f;

        if (!enableDepth)
        {
            return false;
        }

        lock (depthLock)
        {
            byte[] buffer = latestDepthData;

            if (buffer == null || publishedDepthWidth <= 0 || publishedDepthHeight <= 0)
            {
                return false;
            }

            if (!TryMapColorPixelToDepthPixel(
                    x,
                    y,
                    publishedDepthWidth,
                    publishedDepthHeight,
                    out int sampleX,
                    out int sampleY))
            {
                return false;
            }

            int sampleCount = 0;

            for (int offsetY = -DepthSampleWindowRadius; offsetY <= DepthSampleWindowRadius; offsetY++)
            {
                int row = Mathf.Clamp(sampleY + offsetY, 0, publishedDepthHeight - 1) * publishedDepthWidth;

                for (int offsetX = -DepthSampleWindowRadius; offsetX <= DepthSampleWindowRadius; offsetX++)
                {
                    int index =
                        (row + Mathf.Clamp(sampleX + offsetX, 0, publishedDepthWidth - 1)) *
                        BytesPerDepthPixel;

                    ushort millimetres = (ushort)(buffer[index] | (buffer[index + 1] << 8));

                    if (millimetres != 0)
                    {
                        depthSampleWindow[sampleCount] = millimetres;
                        sampleCount++;
                    }
                }
            }

            if (sampleCount == 0)
            {
                return false;
            }

            Array.Sort(depthSampleWindow, 0, sampleCount);
            depthMeters = depthSampleWindow[sampleCount / 2] * 0.001f;

            return depthMeters > 0f;
        }
    }

    private bool TryMapColorPixelToDepthPixel(
        int x,
        int y,
        int width,
        int height,
        out int depthX,
        out int depthY)
    {
        x = Mathf.Clamp(x, 0, ColorWidth - 1);
        y = Mathf.Clamp(y, 0, ColorHeight - 1);

        if (rotateClockwise)
        {
            depthX = y;
            depthY = SensorColorHeight - 1 - x;
        }
        else
        {
            depthX = SensorColorWidth - 1 - y;
            depthY = x;
        }

        return depthX >= 0 && depthX < width && depthY >= 0 && depthY < height;
    }

    private void InitializeKinect()
    {
        try
        {
            Debug.Log("Opening Azure Kinect RGB camera...");

            Device = Device.Open(0);

            Debug.Log("Azure Kinect opened.");

            DeviceConfiguration config =
                new DeviceConfiguration
                {
                    ColorFormat = ImageFormat.ColorBGRA32,
                    ColorResolution = ColorResolution.R720p,
                    DepthMode = enableDepth ? DepthMode.NFOV_Unbinned : DepthMode.Off,
                    CameraFPS = FPS.FPS30,
                    SynchronizedImagesOnly = enableDepth
                };

            Debug.Log("Starting Azure Kinect RGB camera...");

            Device.StartCameras(config);

            Debug.Log("Azure Kinect RGB camera started.");

            rotateClockwise = portraitRotation == PortraitRotation.Clockwise90;
            CacheCalibration(Device.GetCalibration());

            lock (colorLock)
            {
                latestColorData = null;
                displayedColorData = null;
                newColorFrameAvailable = false;
            }

            ResetDepthBuffers();

            shuttingDown = false;
            cameraThreadRunning = true;

            cameraThread = new Thread(CameraLoop);
            cameraThread.IsBackground = true;
            cameraThread.Name = "Azure Kinect RGB Camera";
            cameraThread.Start();

            Debug.Log("Azure Kinect camera thread started.");
        }
        catch (Exception ex)
        {
            Debug.LogError("Failed to initialize Azure Kinect:\n" + ex);

            SafeCleanupAfterInitializationFailure();
        }
    }

    // Converts the color intrinsics into the portrait frame so back-projected
    // landmarks line up with the rotated image and the depth index mapping.
    private void CacheCalibration(Calibration sourceCalibration)
    {
        calibration = sourceCalibration;

        ColorFx = 0f;
        ColorFy = 0f;
        ColorCx = 0f;
        ColorCy = 0f;

        if (!enableDepth)
        {
            return;
        }

        float[] parameters = sourceCalibration.ColorCameraCalibration.Intrinsics.Parameters;

        if (parameters == null || parameters.Length < 4)
        {
            return;
        }

        float cx = parameters[0];
        float cy = parameters[1];
        float fx = parameters[2];
        float fy = parameters[3];

        if (fx <= 0f || fy <= 0f)
        {
            return;
        }

        ColorFx = fy;
        ColorFy = fx;

        if (rotateClockwise)
        {
            ColorCx = SensorColorHeight - 1 - cy;
            ColorCy = cx;
        }
        else
        {
            ColorCx = cy;
            ColorCy = SensorColorWidth - 1 - cx;
        }
    }

    private void CameraLoop()
    {
        byte[] captureColorData = null;
        byte[] captureDepthData = null;

        Image registeredDepthImage = null;
        Transformation depthTransformation = null;

        bool depthTransformationAttempted = false;
        bool depthWarningLogged = false;

        TimeSpan captureTimeout = TimeSpan.FromSeconds(2);

        while (cameraThreadRunning)
        {
            Capture capture = null;

            try
            {
                try
                {
                    capture = Device.GetCapture(captureTimeout);
                }
                catch (TimeoutException)
                {
                    continue;
                }

                if (!cameraThreadRunning)
                {
                    break;
                }

                if (capture == null)
                {
                    continue;
                }

                for (int skipped = 0; skipped < 8 && cameraThreadRunning; skipped++)
                {
                    Capture newerCapture = null;

                    try
                    {
                        newerCapture = Device.GetCapture(TimeSpan.Zero);
                    }
                    catch (TimeoutException)
                    {
                        break;
                    }

                    if (newerCapture == null)
                    {
                        break;
                    }

                    if (newerCapture.Color == null ||
                        (enableDepth && newerCapture.Depth == null))
                    {
                        newerCapture.Dispose();
                        continue;
                    }

                    Capture olderCapture = capture;

                    capture = newerCapture;
                    olderCapture.Dispose();
                }

                if (!cameraThreadRunning)
                {
                    break;
                }

                if (capture.Color != null)
                {
                    var colorMemory = capture.Color.Memory;

                    if (captureColorData == null ||
                        captureColorData.Length != colorMemory.Length)
                    {
                        captureColorData = new byte[colorMemory.Length];
                    }

                    RotateColorFrame(colorMemory.Span, captureColorData, rotateClockwise);

                    lock (colorLock)
                    {
                        byte[] reusableBuffer = latestColorData;

                        latestColorData = captureColorData;
                        captureColorData = reusableBuffer;

                        newColorFrameAvailable = true;
                    }
                }

                if (enableDepth && capture.Depth != null)
                {
                    if (!depthTransformationAttempted)
                    {
                        depthTransformationAttempted = true;

                        try
                        {
                            depthTransformation = calibration.CreateTransformation();
                        }
                        catch (Exception ex)
                        {
                            Debug.LogWarning(
                                "KinectManager: depth-to-color registration is unavailable, " +
                                "so body landmarks will not have metric depth:\n" + ex
                            );
                        }
                    }

                    if (depthTransformation != null)
                    {
                        try
                        {
                            if (registeredDepthImage == null)
                            {
                                registeredDepthImage =
                                    new Image(ImageFormat.Depth16, SensorColorWidth, SensorColorHeight);
                            }

                            depthTransformation.DepthImageToColorCamera(
                                capture.Depth,
                                registeredDepthImage
                            );

                            if (CopyDepthImageIntoBuffer(registeredDepthImage, ref captureDepthData))
                            {
                                lock (depthLock)
                                {
                                    byte[] reusableBuffer = latestDepthData;

                                    latestDepthData = captureDepthData;
                                    captureDepthData = reusableBuffer;

                                    publishedDepthWidth = registeredDepthImage.WidthPixels;
                                    publishedDepthHeight = registeredDepthImage.HeightPixels;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            if (!depthWarningLogged)
                            {
                                depthWarningLogged = true;

                                Debug.LogWarning(
                                    "KinectManager: depth-to-color registration failed, " +
                                    "so body landmarks will not have metric depth:\n" + ex
                                );
                            }

                            depthTransformation.Dispose();
                            depthTransformation = null;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (shuttingDown)
                {
                    break;
                }

                lock (errorLock)
                {
                    if (pendingWorkerError == null)
                    {
                        pendingWorkerError = ex.ToString();

                        if (string.IsNullOrWhiteSpace(pendingWorkerError))
                        {
                            pendingWorkerError = "Exception type: " + ex.GetType().FullName;
                        }
                    }
                }

                Thread.Sleep(10);
            }
            finally
            {
                if (capture != null)
                {
                    capture.Dispose();
                    capture = null;
                }
            }
        }

        if (registeredDepthImage != null)
        {
            registeredDepthImage.Dispose();
        }

        if (depthTransformation != null)
        {
            depthTransformation.Dispose();
        }
    }

    private static void RotateColorFrame(ReadOnlySpan<byte> source, byte[] destination, bool clockwise)
    {
        for (int y = 0; y < SensorColorHeight; y++)
        {
            int sourceOffset = y * SensorColorWidth * ColorBytesPerPixel;
            int destinationX = clockwise ? SensorColorHeight - 1 - y : y;
            int destinationY = clockwise ? 0 : SensorColorWidth - 1;
            int destinationOffset =
                (destinationY * ColorWidth + destinationX) * ColorBytesPerPixel;
            int destinationStep =
                (clockwise ? ColorWidth : -ColorWidth) * ColorBytesPerPixel;

            for (int x = 0; x < SensorColorWidth; x++)
            {
                destination[destinationOffset] = source[sourceOffset];
                destination[destinationOffset + 1] = source[sourceOffset + 1];
                destination[destinationOffset + 2] = source[sourceOffset + 2];
                destination[destinationOffset + 3] = source[sourceOffset + 3];

                sourceOffset += ColorBytesPerPixel;
                destinationOffset += destinationStep;
            }
        }
    }

    // Copies a Depth16 image into a tightly packed reusable buffer.
    private static bool CopyDepthImageIntoBuffer(Image image, ref byte[] destination)
    {
        int width = image.WidthPixels;
        int height = image.HeightPixels;
        int rowBytes = width * BytesPerDepthPixel;
        int requiredBytes = rowBytes * height;
        int strideBytes = image.StrideBytes;

        Span<byte> source = image.Memory.Span;

        if (width <= 0 || height <= 0 ||
            strideBytes < rowBytes || source.Length < requiredBytes)
        {
            return false;
        }

        if (destination == null || destination.Length != requiredBytes)
        {
            destination = new byte[requiredBytes];
        }

        if (strideBytes == rowBytes)
        {
            source.Slice(0, requiredBytes).CopyTo(destination.AsSpan());
            return true;
        }

        for (int row = 0; row < height; row++)
        {
            source.Slice(row * strideBytes, rowBytes).CopyTo(
                destination.AsSpan(row * rowBytes, rowBytes)
            );
        }

        return true;
    }

    private void ResetDepthBuffers()
    {
        lock (depthLock)
        {
            latestDepthData = null;

            publishedDepthWidth = 0;
            publishedDepthHeight = 0;
        }
    }

    private void Update()
    {
        string error = null;

        lock (errorLock)
        {
            if (pendingWorkerError != null)
            {
                error = pendingWorkerError;
                pendingWorkerError = null;
            }
        }

        if (!string.IsNullOrEmpty(error))
        {
            Debug.LogError("AZURE KINECT CAMERA ERROR:\n" + error);
        }
    }

    private void ShutdownKinect()
    {
        if (shuttingDown)
        {
            return;
        }

        shuttingDown = true;
        cameraThreadRunning = false;

        Debug.Log("Beginning Azure Kinect shutdown...");

        if (cameraThread != null && cameraThread.IsAlive)
        {
            Debug.Log("Waiting for camera worker to finish...");

            bool exited = cameraThread.Join(5000);

            if (!exited)
            {
                Debug.LogError(
                    "Azure Kinect camera worker did not exit within 5 seconds. " +
                    "Native cleanup was skipped to avoid an Editor hang."
                );

                cameraThread = null;
                return;
            }
        }

        cameraThread = null;

        Debug.Log("Camera worker exited.");

        if (Device != null)
        {
            try
            {
                Debug.Log("Stopping Azure Kinect camera...");
                Device.StopCameras();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Camera stop warning: " + ex.Message);
            }

            try
            {
                Debug.Log("Disposing Azure Kinect device...");
                Device.Dispose();
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Device dispose warning: " + ex.Message);
            }

            Device = null;
        }

        lock (colorLock)
        {
            latestColorData = null;
            displayedColorData = null;
            newColorFrameAvailable = false;
        }

        ResetDepthBuffers();

        Debug.Log("Azure Kinect shutdown complete.");
    }

    private void SafeCleanupAfterInitializationFailure()
    {
        cameraThreadRunning = false;
        shuttingDown = true;

        if (Device != null)
        {
            try { Device.StopCameras(); } catch { }
            try { Device.Dispose(); } catch { }
            Device = null;
        }

        lock (colorLock)
        {
            latestColorData = null;
            displayedColorData = null;
            newColorFrameAvailable = false;
        }

        ResetDepthBuffers();
    }

    private void OnDestroy()
    {
        ShutdownKinect();

        if (Instance == this)
        {
            Instance = null;
        }
    }
}
