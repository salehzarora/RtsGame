using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// Diagnostic probe attached at runtime to each Online button by
/// <see cref="MultiplayerLobbyUI"/>'s Start self-heal. Logs whenever a
/// pointer down / click event reaches the button — independently of the
/// Button.onClick listener pipeline.
///
/// Interpretation:
///   • <c>[ClickProbe] PointerDown</c> appears but no
///     <c>[OnlineUI] X clicked</c> → the click reached the UI but no OnClick
///     listener fired (controller / button-wiring problem).
///   • Neither log appears → the click never reached the UI (EventSystem /
///     GraphicRaycaster / raycast-blocker problem).
///   • Both appear → click path is fine; any subsequent failure is
///     downstream (Photon / controller logic).
///
/// Probes are added by the lobby controller every Play (idempotent — only
/// added when missing), so you don't need to manage them by hand. Safe to
/// leave in builds; the runtime cost is one Debug.Log per pointer event,
/// only on the five Online buttons.
/// </summary>
public class OnlineButtonClickProbe : MonoBehaviour,
    IPointerDownHandler, IPointerClickHandler
{
    /// <summary>Short human label appended to every log line.</summary>
    public string label = "?";

    public void OnPointerDown(PointerEventData e)
    {
        Debug.Log($"[ClickProbe] PointerDown on {label}");
    }

    public void OnPointerClick(PointerEventData e)
    {
        Debug.Log($"[ClickProbe] PointerClick on {label}");
    }
}
