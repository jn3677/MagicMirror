using System.Collections;
using Mediapipe;
using Mediapipe.Tasks.Core;
using Mediapipe.Tasks.Vision.Core;
using Mediapipe.Tasks.Vision.HandLandmarker;
using Mediapipe.Unity.Experimental;
using UnityEngine;

public class MediaPipeHandTracker : MonoBehaviour
{
    [Header("Input")]
    [SerializeField]
    private KinectCameraViewer cameraViewer;

    [Header("Tracking")]
    [SerializeField]
    [Range(0f, 1f)]
    private float minDetectionConfidence = 0.5f;

    [SerializeField]
    [Range(0f, 1f)]
    private float minPresenceConfidence = 0.5f;

    [SerializeField]
    [Range(0f, 1f)]
    private float minTrackingConfidence = 0.5f;

    [Header("Coordinate Correction")]
    [SerializeField]
    private bool flipX = true;

    [SerializeField]
    private bool flipY = true;

    [Header("Performance")]
    [SerializeField]
    [Min(1)]
    private int processEveryNFrames = 2;

    public bool HandDetected { get; private set; }

    public Vector2 IndexTipNormalized { get; private set; }

    private HandLandmarker handLandmarker;
    private HandLandmarkerResult result;
    private TextureFrame textureFrame;

    private long timestampMilliseconds = 0;
    private int frameCounter = 0;

    private bool initialized = false;

    private const int IndexFingerTipLandmark = 8;
    private const string ModelPath = "hand_landmarker.bytes";

    private void Start()
    {
        InitializeMediaPipe();
    }

    private void InitializeMediaPipe()
    {
        if (cameraViewer == null)
        {
            Debug.LogError(
                "MediaPipeHandTracker: KinectCameraViewer is not assigned."
            );

            enabled = false;
            return;
        }

        try
        {
            string modelPath = System.IO.Path.Combine(
                Application.streamingAssetsPath,
                "hand_landmarker.bytes"
            );

            Debug.Log(
                $"MediaPipe model path: {modelPath}"
            );

            if (!System.IO.File.Exists(modelPath))
            {
                Debug.LogError(
                    $"MediaPipe hand model was not found at:\n{modelPath}"
                );

                enabled = false;
                return;
            }

            Debug.Log(
                "Initializing MediaPipe Hand Landmarker..."
            );

            var baseOptions = new BaseOptions(
                BaseOptions.Delegate.CPU,
                modelAssetPath: modelPath
            );

            var options = new HandLandmarkerOptions(
                baseOptions,
                runningMode: RunningMode.VIDEO,
                numHands: 1,
                minHandDetectionConfidence:
                    minDetectionConfidence,
                minHandPresenceConfidence:
                    minPresenceConfidence,
                minTrackingConfidence:
                    minTrackingConfidence
            );

            handLandmarker =
                HandLandmarker.CreateFromOptions(
                    options,
                    null
                );

            result =
                HandLandmarkerResult.Alloc(1);

            textureFrame = new TextureFrame(
                KinectManager.ColorWidth,
                KinectManager.ColorHeight,
                TextureFormat.RGBA32
            );

            timestampMilliseconds = 0;
            initialized = true;

            Debug.Log(
                "MediaPipe Hand Landmarker initialized successfully."
            );
        }
        catch (System.Exception ex)
        {
            Debug.LogError(
                "Failed to initialize MediaPipe Hand Landmarker:\n" +
                ex
            );

            initialized = false;
            enabled = false;
        }
    }

    private void Update()
    {
        if (!initialized ||
            handLandmarker == null ||
            textureFrame == null ||
            cameraViewer == null)
        {
            return;
        }

        Texture2D sourceTexture =
            cameraViewer.ColorTexture;

        if (sourceTexture == null)
        {
            HandDetected = false;
            return;
        }

        frameCounter++;

        if (frameCounter % processEveryNFrames != 0)
        {
            return;
        }

        DetectHand(sourceTexture);
    }

    private void DetectHand(Texture2D sourceTexture)
    {
        try
        {
            textureFrame.ReadTextureOnCPU(
                sourceTexture,
                false,
                false
            );

            using Image image =
                textureFrame.BuildCPUImage();

            long newTimestamp =
                (long)(
                    Time.realtimeSinceStartupAsDouble *
                    1000.0
                );

            if (newTimestamp <= timestampMilliseconds)
            {
                newTimestamp =
                    timestampMilliseconds + 1;
            }

            timestampMilliseconds =
                newTimestamp;

            var imageProcessingOptions =
                new ImageProcessingOptions();

            bool detected =
                handLandmarker.TryDetectForVideo(
                    image,
                    timestampMilliseconds,
                    imageProcessingOptions,
                    ref result
                );

            if (!detected ||
                result.handLandmarks == null ||
                result.handLandmarks.Count == 0)
            {
                HandDetected = false;
                return;
            }

            var landmarks =
                result.handLandmarks[0].landmarks;

            if (landmarks == null ||
                landmarks.Count <=
                    IndexFingerTipLandmark)
            {
                HandDetected = false;
                return;
            }

            var indexTip =
                landmarks[IndexFingerTipLandmark];

            float x = indexTip.x;
            float y = indexTip.y;

            if (flipX)
            {
                x = 1f - x;
            }

            if (flipY)
            {
                y = 1f - y;
            }

            IndexTipNormalized =
                new Vector2(
                    Mathf.Clamp01(x),
                    Mathf.Clamp01(y)
                );

            HandDetected = true;
        }
        catch (System.Exception ex)
        {
            HandDetected = false;

            Debug.LogError(
                "MediaPipe hand detection failed:\n" +
                ex
            );
        }
    }

    private void OnDisable()
    {
        HandDetected = false;
        IndexTipNormalized = Vector2.zero;
    }

    private void OnDestroy()
    {
        initialized = false;
        HandDetected = false;

        if (textureFrame != null)
        {
            textureFrame.Dispose();
            textureFrame = null;
        }

        if (handLandmarker != null)
        {
            handLandmarker.Close();
            handLandmarker = null;
        }
    }
}