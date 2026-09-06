using SexyBiscuit.Engine.Core;
using SexyBiscuit.Engine.Input;

namespace Cookies.InputMapping;

/// <summary>
/// The turn-on for rebindable controls. Drop one on any actor in the start scene: it loads the
/// action map, applies whatever the player has rebound, and sets the look defaults.
/// </summary>
/// <remarks>
/// Everything else in the game keeps asking the input manager for actions by name, so nothing has
/// to know this component exists.
/// </remarks>
public sealed class InputSettings : Component
{
    /// <summary>The action map to load, relative to the project. Empty keeps the engine's default.</summary>
    public string ActionMapAsset { get; set; } = "Config/Cookies/input-mapping/Actions.json";

    /// <summary>Whether to apply the bindings the player has changed.</summary>
    public bool LoadSavedBindings { get; set; } = true;

    /// <summary>Mouse look, in degrees per pixel.</summary>
    public float LookSensitivity { get; set; } = 0.15f;

    /// <summary>Right-stick look, in degrees per second at full deflection.</summary>
    public float GamepadLookSpeed { get; set; } = 180f;

    /// <summary>Inverts vertical look, for the people who fly aeroplanes.</summary>
    public bool InvertLookY { get; set; }

    /// <summary>How far a stick must move before it counts, from 0 to 1.</summary>
    public float GamepadDeadZone { get; set; } = 0.15f;

    /// <summary>Whether the map and the saved bindings were found.</summary>
    public bool Loaded { get; private set; }

    /// <summary>What went wrong, when something did.</summary>
    public string? LastError { get; private set; }

    public override void Awake() => Apply();

    /// <summary>Reloads the map and the saved bindings. Safe to call again at any time.</summary>
    public void Apply()
    {
        var input = EngineHost.Current?.Input;
        if (input == null)
        {
            LastError = "there is no engine host, so there is no input manager to configure.";
            return;
        }

        LastError = null;

        if (!string.IsNullOrWhiteSpace(ActionMapAsset))
        {
            string path = ProjectPaths.Resolve(ActionMapAsset);
            if (File.Exists(path))
            {
                try
                {
                    input.LoadActionMap(path);
                }
                catch (Exception ex)
                {
                    LastError = $"{ActionMapAsset} could not be read: {ex.Message}";
                }
            }
            else
            {
                LastError = $"{ActionMapAsset} is missing, so the engine's default map is in use.";
            }
        }

        if (LoadSavedBindings) BindingStore.Load(input);

        for (int i = 0; i < 4; i++) input.GetGamepad(i).DeadZone = GamepadDeadZone;

        Loaded = LastError == null;
    }

    /// <summary>Applies the look settings to a controller, which owns its own copies of them.</summary>
    public void ApplyTo(SexyBiscuit.Engine.Gameplay.PlayerController controller)
    {
        controller.LookSensitivity  = LookSensitivity;
        controller.GamepadLookSpeed = GamepadLookSpeed;
        controller.InvertLookY      = InvertLookY;
    }
}
