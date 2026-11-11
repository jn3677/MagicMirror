using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

// Debug view for the 33 pose landmarks published by MediaPipeBodyTracker: a
// scrollable text grid plus optional dots over the color feed. Everything is
// drawn from a single OnGUI pass, with no per-landmark GameObjects and no extra
// components on any other object.
public class LandmarkDebugDisplay : MonoBehaviour
{
    private const int LandmarkCount = 33;

    private const float RowHeight = 18f;
    private const float HeaderHeight = 22f;

    private static readonly float[] ColumnWidths = { 40f, 70f, 70f, 80f, 80f, 80f, 84f };

    private static readonly string[] ColumnHeaders =
    {
        "i", "lm.x", "lm.y", "cam.x", "cam.y", "cam.z", "depth m"
    };

    [Header("Sources")]
    [SerializeField]
    private MediaPipeBodyTracker bodyTracker;

    // Only used to place the overlay dots over the color feed. Found
    // automatically when left empty.
    [SerializeField]
    private KinectCameraViewer cameraViewer;

    [Header("Panel")]
    [SerializeField]
    private bool showPanel = true;

    [SerializeField]
    private Rect panelRect = new Rect(10f, 10f, 530f, 420f);

    [Header("Overlay")]
    [SerializeField]
    private bool showLandmarkDots = true;

    [SerializeField]
    private float dotSize = 7f;

    [SerializeField]
    private Color depthAvailableColor = Color.green;

    [SerializeField]
    private Color depthMissingColor = Color.red;

    [Header("Overlay Values")]
    [SerializeField]
    private bool showValues = false;

    private Vector2 scrollPosition;

    private Texture2D dotTexture;

    // 1x1 semi opaque black, used as the label background so the text stays
    // readable over the video.
    private Texture2D valueBackgroundTexture;

    private RawImage colorImage;

    // Built on first use inside OnGUI: Unity does not allow GUIStyle to be
    // created outside an OnGUI call.
    private GUIStyle valueStyle;

    // Reused so labelling does not allocate a GUIContent per landmark per frame.
    private readonly GUIContent valueContent = new GUIContent();

    // Reused by GetGuiRect so drawing dots allocates nothing.
    private readonly Vector3[] cornerBuffer = new Vector3[4];

    private void Awake()
    {
        // One 1x1 white texture, tinted per dot with GUI.color.
        dotTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        dotTexture.SetPixel(0, 0, Color.white);
        dotTexture.Apply();

        valueBackgroundTexture = new Texture2D(1, 1, TextureFormat.RGBA32, false);
        valueBackgroundTexture.SetPixel(0, 0, new Color(0f, 0f, 0f, 0.6f));
        valueBackgroundTexture.Apply();
    }

    private void Start()
    {
        if (bodyTracker == null)
        {
            bodyTracker = FindAnyObjectByType<MediaPipeBodyTracker>();
        }

        if (cameraViewer == null)
        {
            cameraViewer = FindAnyObjectByType<KinectCameraViewer>();
        }

        if (cameraViewer != null)
        {
            colorImage = cameraViewer.GetComponent<RawImage>();
        }

        if (bodyTracker == null)
        {
            Debug.LogError(
                "LandmarkDebugDisplay: no MediaPipeBodyTracker in the scene, so the " +
                "landmark display is disabled.",
                this
            );

            enabled = false;
        }
    }

    private void OnGUI()
    {
        if (bodyTracker == null)
        {
            return;
        }

        // Dots first so the panel paints over them.
        if (showLandmarkDots || showValues)
        {
            DrawLandmarkOverlay();
        }

        if (showPanel)
        {
            DrawPanel();
        }
    }

    private void DrawPanel()
    {
        float contentWidth = 0f;

        for (int column = 0; column < ColumnWidths.Length; column++)
        {
            contentWidth += ColumnWidths[column];
        }

        float contentHeight = HeaderHeight + (RowHeight * (LandmarkCount + 1));

        scrollPosition = GUI.BeginScrollView(
            panelRect,
            scrollPosition,
            new Rect(0f, 0f, contentWidth, contentHeight)
        );

        GUI.Label(
            new Rect(0f, 0f, contentWidth, HeaderHeight),
            BuildHeaderText()
        );

        for (int column = 0; column < ColumnHeaders.Length; column++)
        {
            GUI.Label(
                GetCellRect(column, 0f),
                ColumnHeaders[column]
            );
        }

        IReadOnlyList<Vector3> landmarks = bodyTracker.Landmarks;
        IReadOnlyList<Vector3> cameraSpaceLandmarks = bodyTracker.CameraSpaceLandmarks;
        IReadOnlyList<float> landmarkDepths = bodyTracker.LandmarkDepthsMeters;

        for (int i = 0; i < LandmarkCount; i++)
        {
            float rowY = HeaderHeight + (RowHeight * (i + 1));

            bool hasLandmark = landmarks != null && i < landmarks.Count;
            bool hasCameraSpace = cameraSpaceLandmarks != null && i < cameraSpaceLandmarks.Count;

            Vector3 landmark = hasLandmark ? landmarks[i] : Vector3.zero;
            Vector3 cameraSpace = hasCameraSpace ? cameraSpaceLandmarks[i] : Vector3.zero;

            float depth = landmarkDepths != null && i < landmarkDepths.Count
                ? landmarkDepths[i]
                : float.NaN;

            GUI.Label(GetCellRect(0, rowY), i.ToString());
            GUI.Label(GetCellRect(1, rowY), hasLandmark ? landmark.x.ToString("F3") : "--");
            GUI.Label(GetCellRect(2, rowY), hasLandmark ? landmark.y.ToString("F3") : "--");
            GUI.Label(GetCellRect(3, rowY), hasCameraSpace ? cameraSpace.x.ToString("F3") : "--");
            GUI.Label(GetCellRect(4, rowY), hasCameraSpace ? cameraSpace.y.ToString("F3") : "--");
            GUI.Label(GetCellRect(5, rowY), hasCameraSpace ? cameraSpace.z.ToString("F3") : "--");
            GUI.Label(GetCellRect(6, rowY), float.IsNaN(depth) ? "--" : depth.ToString("F3"));
        }

        GUI.EndScrollView();
    }

    // MediaPipeBodyTracker exposes no timestamp of its own, so the timestamp
    // shown here is the value it derives and feeds to the landmarker:
    // (long)(Time.realtimeSinceStartupAsDouble * 1000.0) milliseconds.
    private string BuildHeaderText()
    {
        long timestampMilliseconds = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);

        return string.Format(
            "BodyDetected: {0}    DepthFusionEnabled: {1}    frame {2}    timestamp {3} ms",
            bodyTracker.BodyDetected,
            bodyTracker.DepthFusionEnabled,
            Time.frameCount,
            timestampMilliseconds
        );
    }

    // Draws the landmark dots and, when enabled, the metric value label next to
    // each one. One loop, no per-landmark objects.
    private void DrawLandmarkOverlay()
    {
        if (colorImage == null || dotTexture == null || !bodyTracker.BodyDetected)
        {
            return;
        }

        IReadOnlyList<Vector3> landmarks = bodyTracker.Landmarks;

        if (landmarks == null || landmarks.Count < LandmarkCount)
        {
            return;
        }

        Rect colorRect = GetGuiRect(colorImage.rectTransform);

        if (colorRect.width <= 0f || colorRect.height <= 0f)
        {
            return;
        }

        IReadOnlyList<Vector3> cameraSpaceLandmarks = bodyTracker.CameraSpaceLandmarks;
        IReadOnlyList<float> landmarkDepths = bodyTracker.LandmarkDepthsMeters;

        Color previousColor = GUI.color;
        float halfDot = dotSize * 0.5f;

        for (int i = 0; i < LandmarkCount; i++)
        {
            Vector3 landmark = landmarks[i];

            // Same mapping MediaPipeBodyTracker uses when it samples depth.
            float pixelX = landmark.x * KinectManager.ColorWidth;
            float pixelY = landmark.y * KinectManager.ColorHeight;

            // A landmark can sit outside the frame; skip it rather than draw a
            // dot off the color feed.
            if (pixelX < 0f || pixelX >= KinectManager.ColorWidth ||
                pixelY < 0f || pixelY >= KinectManager.ColorHeight)
            {
                continue;
            }

            // The color feed is displayed mirrored (RawImage uvRect.width = -1),
            // so the dot follows the mirrored video in x. Vertical is unchanged.
            float screenX = colorRect.xMax - ((pixelX / KinectManager.ColorWidth) * colorRect.width);
            float screenY = colorRect.y + ((pixelY / KinectManager.ColorHeight) * colorRect.height);

            bool hasDepth = landmarkDepths != null &&
                i < landmarkDepths.Count &&
                !float.IsNaN(landmarkDepths[i]);

            if (showLandmarkDots)
            {
                GUI.color = hasDepth ? depthAvailableColor : depthMissingColor;

                GUI.DrawTexture(
                    new Rect(screenX - halfDot, screenY - halfDot, dotSize, dotSize),
                    dotTexture
                );

                GUI.color = previousColor;
            }

            if (showValues)
            {
                DrawValueLabel(i, screenX, screenY, halfDot, hasDepth, cameraSpaceLandmarks);
            }
        }

        GUI.color = previousColor;
    }

    // "{i}: {x:F2}, {y:F2}, {z:F2}" in meters, or "{i}: --" when the metrics are
    // unavailable for this landmark.
    private void DrawValueLabel(
        int index,
        float screenX,
        float screenY,
        float halfDot,
        bool hasDepth,
        IReadOnlyList<Vector3> cameraSpaceLandmarks)
    {
        bool hasMetricPosition = bodyTracker.DepthFusionEnabled &&
            hasDepth &&
            cameraSpaceLandmarks != null &&
            index < cameraSpaceLandmarks.Count;

        if (hasMetricPosition)
        {
            Vector3 cameraSpace = cameraSpaceLandmarks[index];

            valueContent.text = string.Format(
                "{0}: {1:F2}, {2:F2}, {3:F2}",
                index,
                cameraSpace.x,
                cameraSpace.y,
                cameraSpace.z
            );
        }
        else
        {
            valueContent.text = string.Format("{0}: --", index);
        }

        EnsureValueStyle();

        Vector2 labelSize = valueStyle.CalcSize(valueContent);

        // Offset to the right and up so the label never covers its own dot.
        GUI.Label(
            new Rect(
                screenX + halfDot + 2f,
                screenY - halfDot - labelSize.y,
                labelSize.x,
                labelSize.y
            ),
            valueContent,
            valueStyle
        );
    }

    // Unity only lets GUIStyle be constructed during OnGUI, so this is called
    // from the draw path rather than from Awake.
    private void EnsureValueStyle()
    {
        if (valueStyle != null)
        {
            return;
        }

        valueStyle = new GUIStyle(GUI.skin.label);
        valueStyle.fontSize = 12;
        valueStyle.alignment = TextAnchor.MiddleLeft;
        valueStyle.padding = new RectOffset(3, 3, 1, 1);
        valueStyle.normal.textColor = Color.white;
        valueStyle.normal.background = valueBackgroundTexture;
    }

    private static Rect GetCellRect(int column, float y)
    {
        float x = 0f;

        for (int i = 0; i < column; i++)
        {
            x += ColumnWidths[i];
        }

        return new Rect(x, y, ColumnWidths[column], RowHeight);
    }

    // The RawImage's on-screen rectangle in GUI space (origin top left), so the
    // dots land on the color feed instead of on raw screen pixels.
    private Rect GetGuiRect(RectTransform rectTransform)
    {
        rectTransform.GetWorldCorners(cornerBuffer);

        Canvas canvas = rectTransform.GetComponentInParent<Canvas>();

        Camera canvasCamera = null;

        if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
        {
            canvasCamera = canvas.worldCamera;
        }

        // Corners: 0 bottom left, 1 top left, 2 top right, 3 bottom right.
        Vector2 bottomLeft = RectTransformUtility.WorldToScreenPoint(canvasCamera, cornerBuffer[0]);
        Vector2 topRight = RectTransformUtility.WorldToScreenPoint(canvasCamera, cornerBuffer[2]);

        float xMin = Mathf.Min(bottomLeft.x, topRight.x);
        float yMin = Mathf.Min(bottomLeft.y, topRight.y);
        float width = Mathf.Abs(topRight.x - bottomLeft.x);
        float height = Mathf.Abs(topRight.y - bottomLeft.y);

        // Screen space has its origin at the bottom left and GUI space at the
        // top left, so only the vertical axis needs converting.
        return new Rect(xMin, Screen.height - (yMin + height), width, height);
    }

    private void OnDestroy()
    {
        if (dotTexture != null)
        {
            Destroy(dotTexture);
            dotTexture = null;
        }

        if (valueBackgroundTexture != null)
        {
            Destroy(valueBackgroundTexture);
            valueBackgroundTexture = null;
        }
    }
}
