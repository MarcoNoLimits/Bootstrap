using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;

public class WorldUIInputBridge : MonoBehaviour
{
    public UIDocument uiDoc;
    public RenderTexture renderTexture;
    public Collider targetCollider;
    [SerializeField] private bool enableHoverDwellClick = true;
    [SerializeField] private float hoverDwellSeconds = 0.16f;
    [SerializeField] private float hoverClickCooldownSeconds = 0.25f;
    [SerializeField] private float selectClickCooldownSeconds = 0.18f;
    [SerializeField] private float rayHitGraceSeconds = 0.12f;
    [SerializeField] private float pointerSmoothing = 22f;

    private XRRayInteractor _currentInteractor;
    private bool _isHovering;

    /// <summary>Last good UI position while hovering the quad — pinch sometimes clears 3D hit on the select frame.</summary>
    private Vector2? _lastHoverPanelPos;
    private VisualElement _lastHoverTarget;
    private float _hoverTargetSinceTime = -1f;
    private float _lastHoverClickTime = -999f;
    private float _lastSelectClickTime = -999f;
    private float _lastValidRayHitTime = -999f;
    private Vector2 _smoothedPanelPos;
    private bool _hasSmoothedPanelPos;

    // Triggered by XRSimpleInteractable
    public void OnSelectEntered(SelectEnterEventArgs args)
    {
        if (args.interactorObject is not XRRayInteractor rayInteractor) return;
        if (Time.time < _lastSelectClickTime + selectClickCooldownSeconds) return;

        Vector2? panelPos = null;

        if (TryGetUiRaycastHit(rayInteractor, out RaycastHit hit) && hit.collider == targetCollider)
            panelPos = UpdateSmoothedPanelPosition(HitToPanelPosition(hit));
        else if (_hasSmoothedPanelPos)
            panelPos = _smoothedPanelPos;
        else if (_lastHoverPanelPos.HasValue)
            panelPos = _lastHoverPanelPos;

        if (!panelPos.HasValue) return;

        ClickUIAtPanelPosition(panelPos.Value);
        _lastSelectClickTime = Time.time;
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
            _lastHoverPanelPos = null;
            _lastHoverTarget = null;
            _hoverTargetSinceTime = -1f;
            _hasSmoothedPanelPos = false;
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
            if (TryGetUiRaycastHit(_currentInteractor, out RaycastHit hit) && hit.collider == targetCollider)
            {
                Vector2 panelPos = UpdateSmoothedPanelPosition(HitToPanelPosition(hit));
                _lastHoverPanelPos = panelPos;
                _lastValidRayHitTime = Time.time;
                MovePointer(panelPos);
                MaybeHoverDwellClick(panelPos);
            }
            else if (_hasSmoothedPanelPos && Time.time - _lastValidRayHitTime <= rayHitGraceSeconds)
            {
                // Preserve pointer stability when right-hand pinch briefly drops 3D ray hits.
                MovePointer(_smoothedPanelPos);
            }
        }
    }

    private Vector2 UpdateSmoothedPanelPosition(Vector2 rawPanelPos)
    {
        if (!_hasSmoothedPanelPos)
        {
            _smoothedPanelPos = rawPanelPos;
            _hasSmoothedPanelPos = true;
            return _smoothedPanelPos;
        }

        float t = Mathf.Clamp01(pointerSmoothing * Time.deltaTime);
        _smoothedPanelPos = Vector2.Lerp(_smoothedPanelPos, rawPanelPos, t);
        return _smoothedPanelPos;
    }

    private void MaybeHoverDwellClick(Vector2 panelPos)
    {
        if (!enableHoverDwellClick || uiDoc == null || renderTexture == null) return;
        if (Time.time < _lastHoverClickTime + hoverClickCooldownSeconds) return;

        IPanel panel = uiDoc.rootVisualElement.panel;
        if (panel == null) return;

        VisualElement picked = panel.Pick(panelPos);
        VisualElement target = ResolveClickableTarget(picked);
        if (target == null) return;

        if (!ReferenceEquals(target, _lastHoverTarget))
        {
            _lastHoverTarget = target;
            _hoverTargetSinceTime = Time.time;
            return;
        }

        if (_hoverTargetSinceTime < 0f || Time.time - _hoverTargetSinceTime < hoverDwellSeconds)
        {
            return;
        }

        ClickUIAtPanelPosition(panelPos);
        _lastHoverClickTime = Time.time;
        _hoverTargetSinceTime = Time.time;
    }

    private void MovePointer(Vector2 panelPos)
    {
        if (uiDoc == null || renderTexture == null) return;

        IPanel panel = uiDoc.rootVisualElement.panel;
        VisualElement picked = panel.Pick(panelPos);
        VisualElement target = ResolveClickableTarget(picked) ?? picked;
        if (target == null) return;

        using (var e = PointerMoveEvent.GetPooled())
        {
            e.target = target;
            SetEventLocalPosition(e, panelPos);
            SetEventSystemFields(e, 1, 0, Target.TransformPoint(panelPos));
            target.SendEvent(e);
        }
    }

    /// <summary>
    /// BoxCollider ray hits do not populate textureCoord (only MeshCollider does).
    /// Derive panel pixels from the hit in the quad's local space instead.
    /// </summary>
    private Vector2 HitToPanelPosition(RaycastHit hit)
    {
        float px;
        float pyPanel;

        if (hit.collider is MeshCollider)
        {
            px = hit.textureCoord.x * renderTexture.width;
            pyPanel = (1f - hit.textureCoord.y) * renderTexture.height;
        }
        else
        {
            Transform tr = hit.collider.transform;
            Vector3 local = tr.InverseTransformPoint(hit.point);
            float u = local.x + 0.5f;
            float v = local.y + 0.5f;
            px = u * renderTexture.width;
            pyPanel = (1f - v) * renderTexture.height;
        }

        return new Vector2(px, pyPanel);
    }

    private static VisualElement ResolveClickableTarget(VisualElement picked)
    {
        if (picked == null) return null;
        if (picked is Button) return picked;
        return picked.GetFirstAncestorOfType<Button>();
    }

    private void ClickUIAtPanelPosition(Vector2 panelPos)
    {
        if (uiDoc == null || renderTexture == null) return;

        IPanel panel = uiDoc.rootVisualElement.panel;
        VisualElement picked = panel.Pick(panelPos);
        VisualElement target = ResolveClickableTarget(picked) ?? picked;

        if (target == null) return;

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

    private bool TryGetUiRaycastHit(XRRayInteractor rayInteractor, out RaycastHit hit)
    {
        hit = default;

        if (rayInteractor.TryGetCurrent3DRaycastHit(out hit) && hit.collider == targetCollider)
            return true;

        Transform origin = rayInteractor.rayOriginTransform != null
            ? rayInteractor.rayOriginTransform
            : rayInteractor.transform;
        Vector3 start = origin.position;
        Vector3 end = rayInteractor.rayEndPoint;
        Vector3 toEnd = end - start;
        float lineLen = toEnd.magnitude;
        if (lineLen > 1e-4f)
        {
            Vector3 dir = toEnd / lineLen;
            if (Physics.Raycast(start, dir, out hit, lineLen + 0.1f) && hit.collider == targetCollider)
                return true;
        }

        Ray ray = new Ray(start, origin.forward);
        float maxDist = rayInteractor.maxRaycastDistance > 0 ? rayInteractor.maxRaycastDistance : 30f;
        return Physics.Raycast(ray, out hit, maxDist) && hit.collider == targetCollider;
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
                ClickUIAtPanelPosition(HitToPanelPosition(hit));
            }
        }
    }
}
