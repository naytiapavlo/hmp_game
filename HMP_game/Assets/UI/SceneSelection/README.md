# Scene selection

Implements the scene-selection portion of the first-phase plan: Main Menu → Scene Select → Office load. English interface, three spacious cards, one available environment and two disabled COMING SOON placeholders. It reuses the main menu's red/charcoal rounded controls and Liberation Sans typography.

## Flow and editing

- Initial scene: `Assets/Scenes/初始界面.unity`. The main menu's Start Training event opens this window.
- Click OFFICE to select it. The red border, SELECTED badge and confirmation button become active.
- Back, close and Escape return to the main menu. Reopening clears the previous selection.
- Start Training asynchronously loads `Assets/Scenes/办公室场景.unity` in Single mode with a progress overlay. This scene is already enabled in Build Settings. Subsequent training stages belong to the office scene's level logic.
- Editable prefab: `SceneSelectionUI.prefab`; English text and controls are individual uGUI/TextMesh Pro objects, using a 1920 × 1080 reference canvas.
- Main-menu references are scene-instance overrides; when using this prefab in another scene, assign `mainMenuContent` and `returnFocus`, then connect the menu event to `SceneSelectUI.Open`.
- `WindowContent` starts inactive. Enable it temporarily in Edit Mode to preview the layout, and restore it to inactive before saving.

## Assets

- OfficePreview.png is an unchanged copy of the user-supplied project image `ui/主界面/17f0ca13-acfe-4473-80b9-28a4ab436dbd.png`. It preserves the full image and aspect ratio.
- Typography uses the existing Liberation Sans SDF and main-menu Body material. Font license: `Assets/TextMesh Pro/Fonts/LiberationSans - OFL.txt` (SIL OFL 1.1).
- Rounded shapes, lock icons and progress sprite are original project-local geometry/code. No external art is added.

## Installation

`Tools > HM Protection > Create Scene Selection UI` is a one-time installer for the initial scene and refuses duplicate installation. It backs up the live scene under `Library/SceneSelectionBackup`, verifies non-UI scene content is unchanged, and writes `Library/HMPSceneSelectionReport.json` including text-overflow checks.
