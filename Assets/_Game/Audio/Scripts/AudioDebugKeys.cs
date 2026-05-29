using UnityEngine;

/// <summary>
/// Optional dev aid: press F8 to play the UI-click sound, F9 to play an
/// explosion at the camera, so you can confirm the audio pipeline is wired
/// without setting up a whole scenario. Purely cosmetic and read-only — it only
/// reads two otherwise-unused function keys and calls the null-safe
/// <see cref="AudioManager"/> facade, so it can never affect gameplay or
/// networking.
///
/// Added to the AudioManager GameObject by Tools → RTS → Audio → Generate And
/// Assign Placeholder Test Audio. Safe to delete the component when you're done
/// testing.
/// </summary>
[DisallowMultipleComponent]
public class AudioDebugKeys : MonoBehaviour
{
    [Tooltip("Master switch for the debug hotkeys. Turn off to disable F8/F9 " +
             "without removing the component.")]
    public bool enableHotkeys = true;

    [Tooltip("Plays the UI click sound (2D, always audible).")]
    public KeyCode uiTestKey = KeyCode.F8;

    [Tooltip("Plays an explosion AT the camera — should be clearly audible.")]
    public KeyCode nearTestKey = KeyCode.F9;

    [Tooltip("Plays an explosion FAR beyond its max distance — should be silent " +
             "(culled), proving the distance falloff works.")]
    public KeyCode farTestKey = KeyCode.F10;

    private void Update()
    {
        if (!enableHotkeys) return;

        if (Input.GetKeyDown(uiTestKey))
        {
            AudioManager.Sfx(GameSound.UIButtonClick);
            Debug.Log("[AudioDebug] F8 → UIButtonClick (2D)");
        }

        if (Input.GetKeyDown(nearTestKey))
        {
            AudioManager.SfxAt(GameSound.Explosion, ListenerPos());
            Debug.Log("[AudioDebug] F9 → Explosion at camera (near — should be loud)");
        }

        if (Input.GetKeyDown(farTestKey))
        {
            // Place it well beyond the explosion max distance so it gets culled.
            float maxDist = AudioManager.Instance != null
                ? AudioManager.Instance.explosion3DMaxDistance
                : 110f;
            Vector3 dir = Camera.main != null ? Camera.main.transform.forward : Vector3.forward;
            Vector3 farPos = ListenerPos() + dir * (maxDist + 40f);
            AudioManager.SfxAt(GameSound.Explosion, farPos);
            Debug.Log($"[AudioDebug] F10 → Explosion {maxDist + 40f:F0}u away " +
                      "(beyond max — should be silent/skipped)");
        }
    }

    private Vector3 ListenerPos()
    {
        return Camera.main != null ? Camera.main.transform.position : transform.position;
    }
}
