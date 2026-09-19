# Main menu UI

Reference: `ui/demo界面/主界面.png` in the repository root.

The current `初始界面` scene contains `MainMenuUI`, an editable uGUI prefab with a 1920 × 1080 reference canvas. Its transparent overlay preserves the existing scene camera, office background, lighting and depth of field. The layout scales to fit smaller viewports without cropping the buttons.

## Editing

- `MenuLayout/Company`, `TitleLine1`, `TitleLine2`, `Subtitle`: editable TextMesh Pro objects.
- Five named button objects each contain an editable `Label` child.
- `MenuButtonGraphic` draws rounded borders and gradient faces procedurally, with no screenshot or background baked into the buttons. Style and corner radius are editable on the component.
- Standard Button colour transitions provide hover, selection and pressed feedback. Up/down navigation uses explicit neighbours; the existing Input System handles UI input.
- `MainMenuView` exposes four UnityEvents. In the initial scene, Start Training opens the scene-selection window. Training Records, Device Settings and How to Play retain their extension hooks. Exit quits a built player and stops Play Mode in the editor.

## Assets and licensing

- Button geometry and layout: original project-local code, created 2026-09-19.
- Font: the project's existing Liberation Sans / TextMesh Pro asset; SIL OFL 1.1, license retained at `Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt`.
- `Title.mat` and `Body.mat`: local material variants; the shared font material is unchanged.
- Company text: `hmprotection inc`; no external marks or imagery added.

## Installation and recovery

`Tools > HM Protection > Create Main Menu UI` installs once into the active `初始界面` scene and refuses duplicate installs or other scenes. It is not an automatic startup rebuild. `Library/HMPMainMenu.request` is an explicit local one-shot automation trigger.

Before installation, the live scene is backed up under `Library/MainMenuUIBackup/`. The installer validates that non-UI components are unchanged and saves `Library/HMPMainMenuReport.json`. The existing `New Text` placeholder is disabled, not deleted. All background content remains intact.
