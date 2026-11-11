using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class HandController : MonoBehaviour
{
    [Header("References")]
    public RectTransform handCursor;
    public MediaPipeHandTracker handTracker;
    public MediaPipeBodyTracker bodyTracker;

    [Header("Testing")]
    public bool useMouseFallback = false;

    [Header("Cursor")]
    [Range(0f, 1f)]
    public float smoothing = 0.35f;

    public Vector2 HandScreenPosition { get; private set; }

    public bool HasTrackingInput { get; private set; }

    private bool hasPosition = false;
    private bool cursorVisible = false;
    private bool cursorSuppressed = false;

    // Hides the cursor even while the hand is tracked, for modes that only
    // reveal it over a specific control.
    public void SetCursorSuppressed(bool suppressed)
    {
        cursorSuppressed = suppressed;

        if (suppressed)
        {
            SetCursorVisible(false);
        }
    }

    private void Start()
    {
        SetCursorVisible(false);
    }

    private void Update()
    {
        Vector2 targetPosition;

        if (handTracker != null &&
            handTracker.HandDetected)
        {
            Vector2 normalized =
                handTracker.IndexTipNormalized;

            targetPosition = new Vector2(
                normalized.x * Screen.width,
                normalized.y * Screen.height
            );

            HasTrackingInput = true;

            SetCursorVisible(true);
        }

        else if (useMouseFallback && Mouse.current != null)
        {
            targetPosition =
                Mouse.current.position.ReadValue();

            HasTrackingInput = true;

            SetCursorVisible(true);
        }

        else if (TryGetBodyWrist(out targetPosition))
        {
            HasTrackingInput = true;

            SetCursorVisible(true);
        }

        else
        {
            HasTrackingInput = false;
            hasPosition = false;

            SetCursorVisible(false);

            return;
        }

        if (!hasPosition)
        {
            HandScreenPosition = targetPosition;
            hasPosition = true;
        }
        else
        {
            HandScreenPosition = Vector2.Lerp(
                HandScreenPosition,
                targetPosition,
                smoothing
            );
        }

        if (handCursor != null)
        {
            handCursor.position =
                HandScreenPosition;
        }
    }

    // While the avatar is shown the hand tracker is off, so the raised wrist
    // from the body tracker drives the cursor instead.
    private bool TryGetBodyWrist(out Vector2 position)
    {
        position = default;

        if (bodyTracker == null || !bodyTracker.enabled || !bodyTracker.BodyDetected)
        {
            return false;
        }

        IReadOnlyList<Vector3> landmarks = bodyTracker.Landmarks;

        if (landmarks == null || landmarks.Count < 17)
        {
            return false;
        }

        Vector3 left = landmarks[15];
        Vector3 right = landmarks[16];

        bool hasLeft = IsInFrame(left);
        bool hasRight = IsInFrame(right);

        if (!hasLeft && !hasRight)
        {
            return false;
        }

        Vector3 wrist = !hasLeft ? right
            : !hasRight ? left
            : left.y <= right.y ? left : right;

        position = new Vector2(
            (1f - wrist.x) * Screen.width,
            (1f - wrist.y) * Screen.height
        );

        return true;
    }

    private static bool IsInFrame(Vector3 landmark)
    {
        return landmark.x >= 0f && landmark.x <= 1f &&
            landmark.y >= 0f && landmark.y <= 1f;
    }

    private void SetCursorVisible(bool visible)
    {
        if (handCursor == null)
        {
            return;
        }

        visible &= !cursorSuppressed;

        if (cursorVisible == visible)
        {
            return;
        }

        cursorVisible = visible;

        handCursor.gameObject.SetActive(visible);
    }
}