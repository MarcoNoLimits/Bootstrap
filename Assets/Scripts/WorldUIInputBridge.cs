using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

public class WorldUIInputBridge : MonoBehaviour
{
    public UIDocument uiDoc;
    public RenderTexture renderTexture;
    public Collider targetCollider;

    private XRRayInteractor _currentInteractor;
    private bool _isHovering;

    // Triggered by XRSimpleInteractable
    public void OnSelectEntered(SelectEnterEventArgs args)
    {
        // On Select (Click), we use the interactor that triggered it
        if (args.interactorObject is XRRayInteractor rayInteractor)
        {
            if (rayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
            {
                if (hit.collider == targetCollider)
                {
                    Vector2 uv = hit.textureCoord;
                    ClickUI(uv);
                }
            }
        }
    }

    public void OnHoverEntered(HoverEnterEventArgs args)
    {
        if (args.interactorObject is XRRayInteractor rayInteractor)
        {
            _currentInteractor = rayInteractor;
            _isHovering = true;
        }
    }

    public void OnHoverExited(HoverExitEventArgs args)
    {
        if ((object)args.interactorObject == _currentInteractor)
        {
            _isHovering = false;
            _currentInteractor = null;
        }
    }

    private void Update()
    {
        // Fallback for Editor testing with Mouse
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            PerformClick(Mouse.current.position.ReadValue());
        }

        // Process Hover
        if (_isHovering && _currentInteractor != null)
        {
            if (_currentInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
            {
                if (hit.collider == targetCollider)
                {
                    Vector2 uv = hit.textureCoord;
                    MovePointer(uv);
                }
            }
        }
    }

    private void MovePointer(Vector2 uv)
    {
        if (uiDoc == null || renderTexture == null) return;
        Vector2 panelPos = new Vector2(uv.x * renderTexture.width, (1 - uv.y) * renderTexture.height);
        
        IPanel panel = uiDoc.rootVisualElement.panel;
        VisualElement target = panel.Pick(panelPos);

        if (target != null)
        {
            using (var e = PointerMoveEvent.GetPooled())
            {
                e.target = target;
                SetEventLocalPosition(e, panelPos);
                SetEventSystemFields(e, 1, 0, Target.TransformPoint(panelPos)); 
                // Note: UIElements expects world position usually? or Local? 
                // 'position' is usually world/screen space. 'localPosition' is local.
                // We set 'position' to panelPos (which is effectively screen space for the panel).
                // But we must use reflection.
                target.SendEvent(e);
            }
        }
    }

    private void ClickUI(Vector2 uv)
    {
        if (uiDoc == null || renderTexture == null) return;
        Vector2 panelPos = new Vector2(uv.x * renderTexture.width, (1 - uv.y) * renderTexture.height);
        
        IPanel panel = uiDoc.rootVisualElement.panel;
        VisualElement target = panel.Pick(panelPos);
        
        if (target != null)
        {
            using (var e = PointerDownEvent.GetPooled())
            {
                e.target = target;
                SetEventLocalPosition(e, panelPos);
                SetEventSystemFields(e, 1, 0, panelPos); 
                target.SendEvent(e);
            }

            using (var e = PointerUpEvent.GetPooled())
            {
                e.target = target;
                SetEventLocalPosition(e, panelPos);
                SetEventSystemFields(e, 1, 0, panelPos);
                target.SendEvent(e);
            }

            using (var e = ClickEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
            
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }
    }

    // --- Reflection Helpers to Bypass Read-Only Fields ---
    private static void SetEventLocalPosition<T>(PointerEventBase<T> e, Vector3 pos) where T : PointerEventBase<T>, new()
    {
        // 'position' property has protected setter.
        // We can access it via reflection on the base type or just set 'm_Position' if strictly internal.
        // Property is usually safer if it exists.
        var prop = typeof(PointerEventBase<T>).GetProperty("position");
        if (prop != null) prop.SetValue(e, (Vector3)pos);
        
        var propLocal = typeof(PointerEventBase<T>).GetProperty("localPosition");
        if (propLocal != null) propLocal.SetValue(e, (Vector3)pos);
    }
    
    private static void SetEventSystemFields<T>(PointerEventBase<T> e, int pointerId, int button, Vector3 pos) where T : PointerEventBase<T>, new()
    {
        var t = typeof(PointerEventBase<T>);
        
        // Pointer ID
        var propId = t.GetProperty("pointerId");
        if (propId != null) propId.SetValue(e, pointerId);
        
        // Button
        var propBtn = t.GetProperty("button");
        if (propBtn != null) propBtn.SetValue(e, button);
        
        // Position again just in case (sometimes logic differs)
        var propPos = t.GetProperty("position");
        if (propPos != null) propPos.SetValue(e, pos);
    }
    // Need a dummy Target transform reference? implementation detail:
    // UI Toolkit uses 'panel' coordinates as 'screen' coordinates in Runtime panels often.
    private Transform Target => uiDoc.transform; // Just a placeholder if needed, but we used panelPos directly.

    private void PerformClick(Vector2 screenPos)
    {
        Ray ray = Camera.main.ScreenPointToRay(screenPos);
        if (Physics.Raycast(ray, out RaycastHit hit))
        {
             if (hit.collider == targetCollider)
            {
                Vector2 uv = hit.textureCoord;
                ClickUI(uv);
            }
        }
    }
}
