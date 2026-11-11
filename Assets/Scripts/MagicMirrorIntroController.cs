
using UnityEngine;
using UnityEngine.Video;

public class MagicMirrorIntroController : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private MediaPipeHandTracker handTracker;
    [SerializeField] private GameObject introVideo;
    [SerializeField] private VideoPlayer videoPlayer;
    [SerializeField] private GameObject carouselRoot;

    [Header("Hand Detection")]
    [SerializeField] private float detectionDuration = 0.5f;

    private float detectionTimer = 0f;
    private bool selectionMode = false;

    private void Start()
    {
        if (handTracker == null ||
            introVideo == null ||
            videoPlayer == null ||
            carouselRoot == null)
        {
            Debug.LogError(
                "MagicMirrorIntroController: Missing Inspector reference."
            );
            enabled = false;
            return;
        }

        // The scene stores an absolute URL from the machine the video was
        // authored on, so resolve the clip from StreamingAssets at runtime.
        videoPlayer.url = System.IO.Path.Combine(
            Application.streamingAssetsPath,
            "Videos",
            "intro-video-final.webm"
        );

        ShowIntro();
    }

    private void Update()
    {
        if (selectionMode)
            return;

        if (handTracker.HandDetected)
        {
            detectionTimer += Time.deltaTime;

            if (detectionTimer >= detectionDuration)
            {
                ShowCarousel();
            }
        }
        else
        {
            detectionTimer = 0f;
        }
    }

    private void ShowIntro()
    {
        selectionMode = false;
        detectionTimer = 0f;

        carouselRoot.SetActive(false);
        introVideo.SetActive(true);

        videoPlayer.isLooping = true;
        videoPlayer.Play();

        Debug.Log("Magic Mirror: Intro mode.");
    }

    private void ShowCarousel()
    {
        selectionMode = true;

        videoPlayer.Stop();
        introVideo.SetActive(false);
        carouselRoot.SetActive(true);

        Debug.Log("Magic Mirror: Hand detected, carousel enabled.");
    }

    public void ResetToIntro()
    {
        ShowIntro();
    }
}