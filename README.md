# Bootstrap

A Unity project template designed to bootstrap VR/XR applications with a robust UI system using Unity's UI Toolkit.

## Key Features

- **World Space UI**: Initializes a `UIDocument` in world space, optimized for VR/AR interactions.
- **Explicit Event Binding**: Demonstrates a clean, code-driven approach to binding UI events (similar to C++ frameworks), avoiding inspector spaghetti.
- **UI Toolkit Integration**: Uses `.uxml` for layout and `.uss` for styling, promoting a separation of concerns between logic and presentation.

## Project Structure

- **Assets/Scripts/**: Contains the core application logic.
  - `App.cs`: The main entry point. It initializes the UI, loads resources, and binds event handlers.
- **Assets/Resources/**: Stores dynamic assets loaded at runtime.
  - `UI/MainLayout.uxml`: The visual tree structure for the main interface.
  - `UI/DefaultPanelSettings.asset`: Essential settings for rendering the UI in the scene.
- **Assets/XR/** & **Assets/XRI/**: configuration files and prefabs for Extended Reality (XR) interaction.

## Getting Started

1.  **Open in Unity**: Launch Unity Hub and add this project folder.
2.  **Scene Setup**: Open the main scene in `Assets/Scenes/`.
3.  **Run**: Press Play. The `App` script will automatically:
    -   Instantiate the "MainUI" GameObject.
    -   Load the UI layout and settings.
    -   Position the UI 1.5 meters in front of the camera.

## Usage

Control the application logic by extending `App.cs`. UI elements are queried by name (e.g., `btn-start`, `btn-settings`) and bound to C# delegates or lambdas.

```csharp
// Example from App.cs
var startBtn = _root.Q<Button>("btn-start");
startBtn.clicked += () => Debug.Log("✅ ASR Started");
```
