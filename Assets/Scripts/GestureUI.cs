using System.Collections.Generic;
using UnityEngine;

public class GestureUIManager : MonoBehaviour
{
    public HandController handController;

    // Card N selects avatarRoots[N].
    public DwellSelectionBox[] clothingBoxes;

    [SerializeField] private List<DwellSelectionBox> carouselButtons =
        new List<DwellSelectionBox>();

    [SerializeField] private List<GameObject> avatarRoots = new List<GameObject>();

    [SerializeField] private CarouselController carousel;
    [SerializeField] private AvatarOverlay overlay;
    [SerializeField] private GameObject selectionRoot;
    [SerializeField] private DwellSelectionBox backButton;

    [Header("Tracking Mode")]
    [SerializeField] private MediaPipeHandTracker handTracker;
    [SerializeField] private MediaPipeBodyTracker bodyTracker;
    [SerializeField] private LandmarkDebugDisplay landmarkDebugDisplay;

    private bool mirrorActive;

    void Update()
    {
        if (handController == null)
        {
            return;
        }

        if (!mirrorActive)
        {
            handController.SetCursorSuppressed(false);
        }

        if (!handController.HasTrackingInput)
        {
            CancelHover();
            return;
        }

        Vector2 handPosition = handController.HandScreenPosition;

        if (mirrorActive)
        {
            backButton.UpdateHover(handPosition);
            handController.SetCursorSuppressed(!backButton.IsHovering);
            return;
        }

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.UpdateHover(handPosition);
        }

        foreach (DwellSelectionBox button in carouselButtons)
        {
            button.UpdateHover(handPosition);
        }
    }

    private void CancelHover()
    {
        if (mirrorActive)
        {
            backButton.CancelHover();
            return;
        }

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.CancelHover();
        }

        foreach (DwellSelectionBox button in carouselButtons)
        {
            button.CancelHover();
        }
    }

    public void OnPreviousClicked()
    {
        if (!mirrorActive)
        {
            carousel?.Previous();
        }
    }

    public void OnNextClicked()
    {
        if (!mirrorActive)
        {
            carousel?.Next();
        }
    }

    public void OnClothingSelected(DwellSelectionBox selectedBox)
    {
        if (mirrorActive)
        {
            return;
        }

        int index = System.Array.IndexOf(clothingBoxes, selectedBox);

        if (index < 0 || index >= avatarRoots.Count || avatarRoots[index] == null)
        {
            Debug.LogError(
                $"GestureUIManager: {selectedBox.name} has no avatar assigned.",
                this
            );

            return;
        }

        mirrorActive = true;

        selectionRoot.SetActive(false);
        backButton.gameObject.SetActive(true);
        backButton.ResetSelection();

        ShowAvatar(avatarRoots[index]);
        SwitchToBodyTracking();

        Debug.Log($"Magic Mirror: showing {avatarRoots[index].name}.");
    }

    public void OnBackClicked()
    {
        if (!mirrorActive)
        {
            return;
        }

        mirrorActive = false;

        HideAvatar();
        SwitchToHandTracking();

        backButton.gameObject.SetActive(false);
        selectionRoot.SetActive(true);
        carousel.ResetToStart();

        foreach (DwellSelectionBox box in clothingBoxes)
        {
            box.ResetSelection();
        }

        foreach (DwellSelectionBox button in carouselButtons)
        {
            button.ResetSelection();
        }

        Debug.Log("Magic Mirror: back to selection.");
    }

    private void ShowAvatar(GameObject root)
    {
        HumanoidPoseDriver driver = root.GetComponent<HumanoidPoseDriver>();

        driver.ResetTracking();

        for (int i = 0; i < avatarRoots.Count; i++)
        {
            if (avatarRoots[i] != null)
            {
                avatarRoots[i].SetActive(avatarRoots[i] == root);
            }
        }

        overlay.SetAvatar(root.GetComponentInChildren<Animator>(true), driver);
    }

    private void HideAvatar()
    {
        for (int i = 0; i < avatarRoots.Count; i++)
        {
            if (avatarRoots[i] != null)
            {
                avatarRoots[i].GetComponent<HumanoidPoseDriver>().ResetTracking();
                avatarRoots[i].SetActive(false);
            }
        }

        overlay.SetAvatar(null, null);
    }

    private void SwitchToBodyTracking()
    {
        if (handTracker != null)
        {
            handTracker.enabled = false;
        }

        if (bodyTracker != null)
        {
            bodyTracker.enabled = true;
        }

        if (landmarkDebugDisplay != null)
        {
            landmarkDebugDisplay.enabled = true;
        }
    }

    private void SwitchToHandTracking()
    {
        if (bodyTracker != null)
        {
            bodyTracker.enabled = false;
        }

        if (handTracker != null)
        {
            handTracker.enabled = true;
        }

        if (landmarkDebugDisplay != null)
        {
            landmarkDebugDisplay.enabled = false;
        }
    }
}
