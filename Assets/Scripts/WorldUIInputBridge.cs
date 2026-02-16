using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;

public class WorldUIInputBridge : MonoBehaviour
{
    public UIDocument uiDoc;
    public RenderTexture renderTexture;
    public Collider targetCollider;

    private void Update()
    {
        // Simple Mouse/Touch fallback for editor testing
        if (Mouse.current.leftButton.wasPressedThisFrame)
        {
            PerformClick(Mouse.current.position.ReadValue());
        }
    }

    // This method should be called by the XR Simple Interactor's "Select" event
    public void OnSelect()
    {
        // For XRI, we need the hit point. 
        // Typically, you'd use OnSelectEntered(SelectEnterEventArgs args) and get the interactor's raycast hit.
        // However, for simplicity here, we assume the user is gazing/pointing at it.
        // A better approach for XRI is to implement IPointerClickHandler if using XRI's UI support? No, that's for Canvas.
        
        // We will just do a Raycast from Camera for now to find UV.
        Ray ray = new Ray(Camera.main.transform.position, Camera.main.transform.forward);
        if (Physics.Raycast(ray, out RaycastHit hit, 10f))
        {
            if (hit.collider == targetCollider)
            {
                Vector2 uv = hit.textureCoord;
                ClickUI(uv);
            }
        }
    }

    private void ClickUI(Vector2 uv)
    {
        if (uiDoc == null || renderTexture == null) return;

        // Convert UV to Panel Coordinates
        Vector2 panelPos = new Vector2(uv.x * renderTexture.width, (1 - uv.y) * renderTexture.height);
        
        // This is the tricky part. UI Toolkit events are complex to forge.
        // But RuntimePanelUtils.ScreenToPanel can be useful if we were in Screen Space.
        
        // Ideally, we want to simulate a click.
        // The proper way is complex.
        // For this task, getting Visuals is key. Interaction on RT without native support is HARD.
        // BUT, we can try to just pick the element at the position and simulate a click.
        
        IPanel panel = uiDoc.rootVisualElement.panel;
        VisualElement target = panel.Pick(panelPos);
        
        if (target != null)
        {
            // Simulate Click
            using (var e = ClickEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
            
            // Also NavigationSubmit
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
            
            Debug.Log($"Clicked on {target.name}");
        }
    }

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
