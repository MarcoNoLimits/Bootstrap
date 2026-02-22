using UnityEngine;
using TMPro;

namespace WizardOfOz
{
    public class UIManager
    {
        private GameObject _uiContainer;
        private TextMeshPro _textComponent;
        private Camera _mainCamera;

        public UIManager()
        {
            _mainCamera = Camera.main;
            CreateWorldSpaceText();
        }

        private void CreateWorldSpaceText()
        {
            _uiContainer = new GameObject("TranslationDisplay");

            // 1. Create a dark background plate so text is readable in AR
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = "BackgroundPlate";
            quad.transform.SetParent(_uiContainer.transform);
            quad.transform.localScale = new Vector3(3f, 0.6f, 1f);
            
            Material bgMat = new Material(Shader.Find("Unlit/Transparent"));
            bgMat.color = new Color(0, 0, 0, 0.8f); // Dark translucent
            quad.GetComponent<Renderer>().material = bgMat;

            // 2. Setup Text component
            GameObject textObj = new GameObject("Text");
            textObj.transform.SetParent(_uiContainer.transform);
            textObj.transform.localPosition = new Vector3(0, 0, -0.01f); // Slightly in front of plate

            _textComponent = textObj.AddComponent<TextMeshPro>();
            _textComponent.alignment = TextAlignmentOptions.Center;
            _textComponent.fontSize = 5.0f;
            _textComponent.text = "READY: Say something in English...";
            _textComponent.color = Color.cyan;
            _textComponent.sortingOrder = 100;

            // Positioning: Shift it up slightly
            if (_mainCamera != null)
            {
                _uiContainer.transform.position = _mainCamera.transform.position + (_mainCamera.transform.forward * 1.5f) + (Vector3.up * 0.5f);
                _uiContainer.transform.LookAt(_mainCamera.transform);
                _uiContainer.transform.Rotate(0, 180, 0); 
            }
            else
            {
                _uiContainer.transform.position = new Vector3(0, 0.5f, 1.5f);
            }
            
            Debug.Log($"[UIManager] World space text created with background at {_uiContainer.transform.position}");
        }

        public void UpdateText(string text)
        {
            if (_textComponent != null)
            {
                _textComponent.text = text;
            }
        }

        public void UpdatePosition()
        {
            // Optional: Tag-along logic if needed, but for now simple fixed position on start
            if (_mainCamera == null) _mainCamera = Camera.main;
            
            if (_mainCamera != null && _uiContainer != null)
            {
                Vector3 targetPos = _mainCamera.transform.position + (_mainCamera.transform.forward * 2.0f);
                _uiContainer.transform.position = Vector3.Lerp(_uiContainer.transform.position, targetPos, Time.deltaTime * 2.0f);
                _uiContainer.transform.LookAt(_mainCamera.transform);
                _uiContainer.transform.Rotate(0, 180, 0);
            }
        }
    }
}
