using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;

// Renders the tracked avatar through the Kinect color intrinsics into a
// transparent texture and composites that texture over the color feed.
[RequireComponent(typeof(RawImage))]
public class AvatarOverlay : MonoBehaviour
{
    [SerializeField]
    private MediaPipeBodyTracker bodyTracker;

    [SerializeField]
    private RawImage colorFeed;

    [SerializeField]
    private HumanoidPoseDriver poseDriver;

    [SerializeField]
    private Animator avatar;

    private RawImage overlayImage;
    private RenderTexture avatarTexture;
    private GameObject backgroundQuad;
    private Material backgroundMaterial;

    private Camera displayCamera;
    private Camera avatarCamera;
    private int previousCullingMask;

    private const float HideDelay = 0.25f;

    private bool cameraConfigured;
    private float lastTrackedTime = float.NegativeInfinity;
    private int trackedFrames;

    private void Start()
    {
        if (bodyTracker == null)
        {
            bodyTracker = FindAnyObjectByType<MediaPipeBodyTracker>();
        }

        if (poseDriver == null)
        {
            poseDriver = FindAnyObjectByType<HumanoidPoseDriver>();
        }

        if (avatar == null)
        {
            avatar = FindAnyObjectByType<Animator>();
        }

        overlayImage = GetComponent<RawImage>();
        displayCamera = Camera.main;

        if (bodyTracker == null || poseDriver == null || avatar == null ||
            displayCamera == null || colorFeed == null)
        {
            Debug.LogError(
                "AvatarOverlay: assign the body tracker, pose driver, and color feed, " +
                "and keep a Main Camera and avatar in the scene.",
                this
            );

            enabled = false;
            return;
        }

        avatarCamera = CreateAvatarCamera(displayCamera);

        avatarTexture = new RenderTexture(
            KinectManager.ColorWidth,
            KinectManager.ColorHeight,
            24,
            RenderTextureFormat.ARGB32
        );
        avatarTexture.filterMode = FilterMode.Bilinear;
        avatarTexture.Create();

        avatarCamera.targetTexture = avatarTexture;
        CreateBackgroundQuad();

        previousCullingMask = displayCamera.cullingMask;
        displayCamera.cullingMask = 0;

        overlayImage.texture = avatarTexture;

        // The feed is displayed mirrored, so mirror the overlay the same way.
        // The render texture is already upright, so it needs no vertical flip.
        overlayImage.uvRect = new Rect(1f, 0f, -1f, 1f);
        overlayImage.raycastTarget = false;
        overlayImage.enabled = false;

        avatarCamera.enabled = true;
        cameraConfigured = true;
    }

    private static Camera CreateAvatarCamera(Camera parent)
    {
        GameObject cameraObject = new GameObject("AvatarCamera");
        cameraObject.transform.SetParent(parent.transform, false);

        Camera camera = cameraObject.AddComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0f, 0f, 0f, 0f);
        camera.cullingMask = ~0;
        camera.nearClipPlane = 0.05f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.enabled = false;
        camera.GetUniversalAdditionalCameraData().renderPostProcessing = false;

        return camera;
    }

    public void SetAvatar(Animator animator, HumanoidPoseDriver driver)
    {
        avatar = animator;
        poseDriver = driver;
        lastTrackedTime = float.NegativeInfinity;
    }

    private void LateUpdate()
    {
        if (!cameraConfigured)
        {
            return;
        }

        KinectManager kinect = KinectManager.Instance;

        bool tracked = avatar != null && avatar.isActiveAndEnabled &&
            bodyTracker.enabled && bodyTracker.BodyDetected &&
            poseDriver != null && poseDriver.isActiveAndEnabled &&
            poseDriver.HasValidPose && poseDriver.IsFitted &&
            kinect != null && kinect.ColorFx > 0f && kinect.ColorFy > 0f;

        if (tracked)
        {
            lastTrackedTime = Time.unscaledTime;
        }

        bool visible = tracked || Time.unscaledTime - lastTrackedTime <= HideDelay;
        overlayImage.enabled = visible;

        if (!visible || !tracked)
        {
            if (Time.frameCount % 60 == 0)
            {
                LogTrackingStatus(kinect);
            }

            return;
        }

        UpdateBackgroundQuad(kinect);

        Matrix4x4 projection = avatarCamera.projectionMatrix;
        projection.m00 = 2f * kinect.ColorFx / KinectManager.ColorWidth;
        projection.m02 = 2f * kinect.ColorCx / KinectManager.ColorWidth - 1f;
        projection.m11 = 2f * kinect.ColorFy / KinectManager.ColorHeight;
        projection.m12 = 1f - 2f * kinect.ColorCy / KinectManager.ColorHeight;
        avatarCamera.projectionMatrix = projection;

        trackedFrames++;

        if (trackedFrames % 60 == 0)
        {
            LogDiagnostics(projection, kinect);
        }
    }

    private void CreateBackgroundQuad()
    {
        Shader backgroundShader = Shader.Find("Hidden/AvatarBackground");

        if (backgroundShader == null)
        {
            Debug.LogError("AvatarOverlay: AvatarBackground shader was not found.", this);
            return;
        }

        backgroundMaterial = new Material(backgroundShader);

        backgroundQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        backgroundQuad.name = "AvatarBackground";
        Destroy(backgroundQuad.GetComponent<Collider>());
        backgroundQuad.transform.SetParent(avatarCamera.transform, false);

        Renderer backgroundRenderer = backgroundQuad.GetComponent<Renderer>();
        backgroundRenderer.sharedMaterial = backgroundMaterial;
        backgroundRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        backgroundRenderer.receiveShadows = false;
    }

    // The camera's clear color alpha is not reliable in URP, so an opaque quad
    // writes transparent black behind the avatar instead.
    private void UpdateBackgroundQuad(KinectManager kinect)
    {
        if (backgroundQuad == null)
        {
            return;
        }

        const float distance = 50f;

        float width = distance * KinectManager.ColorWidth / kinect.ColorFx;
        float height = distance * KinectManager.ColorHeight / kinect.ColorFy;
        float centerX =
            distance * (KinectManager.ColorWidth * 0.5f - kinect.ColorCx) / kinect.ColorFx;
        float centerY =
            distance * (kinect.ColorCy - KinectManager.ColorHeight * 0.5f) / kinect.ColorFy;

        backgroundQuad.transform.localPosition = new Vector3(centerX, centerY, distance);
        backgroundQuad.transform.localScale = new Vector3(width * 1.05f, height * 1.05f, 1f);
    }

    private void LogTrackingStatus(KinectManager kinect)
    {
        if (kinect == null || poseDriver == null)
        {
            return;
        }

        kinect.TryGetDepthAtColorPixel(
            KinectManager.ColorWidth / 2,
            KinectManager.ColorHeight / 2,
            out float centerDepth
        );

        var depths = bodyTracker.LandmarkDepthsMeters;

        string depthInfo = depths != null && depths.Count > 24
            ? $"hipDepths=({depths[23]:F2},{depths[24]:F2}) " +
              $"shoulderDepths=({depths[11]:F2},{depths[12]:F2})"
            : "depths=<none>";

        Debug.Log(
            $"AvatarOverlay status: body={bodyTracker.BodyDetected} " +
            $"pose={poseDriver.HasValidPose} overlay={overlayImage.enabled} " +
            $"centerDepth={centerDepth:F2} {depthInfo}"
        );
    }

    private void LogDiagnostics(Matrix4x4 projection, KinectManager kinect)
    {
        RenderTexture previousTarget = RenderTexture.active;
        RenderTexture.active = avatarTexture;

        Texture2D probe = new Texture2D(1, 1, TextureFormat.RGBA32, false);

        probe.ReadPixels(new Rect(0f, 0f, 1f, 1f), 0, 0);
        probe.Apply();
        Color corner = probe.GetPixel(0, 0);

        // The avatar body covers the centre while tracking, so this separates a
        // blank render texture from an overlay that renders but is not drawn.
        probe.ReadPixels(
            new Rect(avatarTexture.width * 0.5f, avatarTexture.height * 0.5f, 1f, 1f),
            0,
            0
        );
        probe.Apply();
        Color center = probe.GetPixel(0, 0);

        RenderTexture.active = previousTarget;

        Destroy(probe);

        Bounds bounds = GetAvatarBounds(avatar);
        Transform leftShoulder = avatar.GetBoneTransform(HumanBodyBones.LeftUpperArm);
        Transform rightShoulder = avatar.GetBoneTransform(HumanBodyBones.RightUpperArm);

        kinect.TryGetDepthAtColorPixel(
            KinectManager.ColorWidth / 2,
            KinectManager.ColorHeight / 2,
            out float centerDepth
        );

        var depths = bodyTracker.LandmarkDepthsMeters;

        string depthInfo = depths != null && depths.Count > 24
            ? $"hipDepths=({depths[23]:F2},{depths[24]:F2}) " +
              $"shoulderDepths=({depths[11]:F2},{depths[12]:F2})"
            : "depths=<none>";

        string shouldersInfo = leftShoulder != null && rightShoulder != null
            ? $"shouldersPos={(leftShoulder.position + rightShoulder.position) * 0.5f}"
            : "shoulders=<none>";

        Debug.Log(
            $"AvatarOverlay: fx={kinect.ColorFx:F1} fy={kinect.ColorFy:F1} " +
            $"cx={kinect.ColorCx:F1} cy={kinect.ColorCy:F1} " +
            $"m00={projection.m00:F3} m11={projection.m11:F3} " +
            $"cornerAlpha={corner.a:F3} centerAlpha={center.a:F3} " +
            $"near={avatarCamera.nearClipPlane:F2} " +
            $"centerDepth={centerDepth:F2} " +
            $"cameraPos={displayCamera.transform.position} " +
            $"avatarPos={avatar.transform.position} " +
            $"avatarScale={avatar.transform.localScale} " +
            $"boundsSize={bounds.size} boundsCenter={bounds.center} " +
            $"{shouldersInfo} {depthInfo} overlay={overlayImage.enabled}"
        );
    }

    private static Bounds GetAvatarBounds(Animator avatar)
    {
        Renderer[] renderers = avatar.GetComponentsInChildren<Renderer>();

        if (renderers.Length == 0)
        {
            return new Bounds();
        }

        Bounds bounds = renderers[0].bounds;

        for (int i = 1; i < renderers.Length; i++)
        {
            bounds.Encapsulate(renderers[i].bounds);
        }

        return bounds;
    }

    private void OnDestroy()
    {
        if (cameraConfigured)
        {
            if (displayCamera != null)
            {
                displayCamera.cullingMask = previousCullingMask;
            }

            if (avatarCamera != null)
            {
                Destroy(avatarCamera.gameObject);
                avatarCamera = null;
                backgroundQuad = null;
            }
        }

        if (overlayImage != null)
        {
            overlayImage.texture = null;
        }

        if (avatarTexture != null)
        {
            avatarTexture.Release();
            Destroy(avatarTexture);
            avatarTexture = null;
        }

        if (backgroundMaterial != null)
        {
            Destroy(backgroundMaterial);
            backgroundMaterial = null;
        }
    }
}
