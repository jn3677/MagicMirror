using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

// Keeps the mirror in its designed portrait resolution instead of letting the
// Game view stretch the 9:16 feed to whatever aspect the window happens to be.
public class PortraitDisplay : MonoBehaviour
{
    private const uint Width = 1080;
    private const uint Height = 1920;

    private void Awake()
    {
#if UNITY_EDITOR
        PlayModeWindow.SetCustomRenderingResolution(
            Width,
            Height,
            "Magic Mirror 1080x1920"
        );
#else
        Screen.SetResolution((int)Width, (int)Height, false);
#endif
    }
}
