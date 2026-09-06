// ============================================================================
// TutorialManager.js — SexyBiscuit Engine Hello World Tutorial
// ============================================================================
// Attach this script to the "TutorialManager" actor in the Tutorial scene.
//
// This script teaches you the fundamentals of the SexyBiscuit engine:
//   Press 1 — Console Output (Debug.log, Debug.warn, Debug.error)
//   Press 2 — Variable Inspection (reading actor/transform properties)
//   Press 3 — Debug Overlay (F1 key for FPS/memory stats)
//   Press 4 — Understanding onUpdate and Delta Time
//   Press 5 — Finding Actors in the Scene
//   Press 6 — Working with Components
//   Press 7 — Creating and Destroying Actors
//
// Open the Console panel in the editor to see all output.
// ============================================================================

// --- Variable declarations ---

// frameCount tracks the total number of frames rendered while lesson 4 is active.
// We use this to show how many times onUpdate is called per second.
var frameCount = 0;

// lesson4Active is a boolean flag that controls whether lesson 4's per-frame
// logging is currently running. We toggle it on when the player presses "4".
var lesson4Active = false;

// lesson4Timer accumulates elapsed time (in seconds) while lesson 4 runs.
// We use it to log at intervals and to auto-stop after 3 seconds.
var lesson4Timer = 0;

// createdActors stores references to actors we spawn in lesson 7.
// This lets us clean them up when the player presses "7" a second time.
var createdActors = [];

// ============================================================================
// onStart — Called once when the actor first enters the scene.
// This is the ideal place for initialization, welcome messages, and setup.
// ============================================================================
function onStart() {

    // Print a clear visual banner so the user can find the tutorial output easily.
    Debug.log("╔══════════════════════════════════════════════════════════════╗");

    // The title line tells the user exactly what this project is.
    Debug.log("║          WELCOME TO THE SEXYBISCUIT ENGINE TUTORIAL         ║");

    // A second decorative line to close the top of the box.
    Debug.log("╠══════════════════════════════════════════════════════════════╣");

    // Each lesson is listed with its key binding so the user knows what to press.
    Debug.log("║  Press 1 — Console Output                                  ║");

    // Lesson 2 teaches how to read runtime data from actors.
    Debug.log("║  Press 2 — Variable Inspection                             ║");

    // Lesson 3 covers the built-in debug overlay toggled with F1.
    Debug.log("║  Press 3 — Debug Overlay                                   ║");

    // Lesson 4 explains the game loop and delta time — fundamental concepts.
    Debug.log("║  Press 4 — Update Loop & Delta Time                        ║");

    // Lesson 5 shows how to search for actors in the active scene.
    Debug.log("║  Press 5 — Finding Actors                                  ║");

    // Lesson 6 dives into the component architecture used by every actor.
    Debug.log("║  Press 6 — Working with Components                         ║");

    // Lesson 7 demonstrates runtime actor creation and destruction.
    Debug.log("║  Press 7 — Creating & Destroying Actors                    ║");

    // Close the bottom of the visual banner.
    Debug.log("╚══════════════════════════════════════════════════════════════╝");

    // A helpful hint so the user knows where to look for output.
    Debug.log("");
    Debug.log("Open the Console panel in the editor (View > Console) to see output.");
    Debug.log("Press any number key 1-7 to start a lesson. Have fun!");
    Debug.log("");
}

// ============================================================================
// onUpdate — Called every single frame by the engine.
// The parameter "dt" (delta time) is the number of seconds since the last frame.
// At 60 FPS, dt is roughly 0.0167 seconds. We use dt to keep behavior
// consistent regardless of frame rate.
// ============================================================================
function onUpdate(dt) {

    // --- Key Press Detection ---
    // Input.isKeyPressed returns true on the FIRST frame a key is pressed down.
    // This is different from Input.isKeyDown, which returns true every frame
    // the key is held. We use isKeyPressed so each lesson triggers only once.

    // Check if the "1" key was just pressed this frame.
    if (Input.isKeyPressed("1")) {
        // Call the lesson 1 function — console output basics.
        lesson1_ConsoleOutput();
    }

    // Check if the "2" key was just pressed this frame.
    if (Input.isKeyPressed("2")) {
        // Call the lesson 2 function — reading actor properties.
        lesson2_VariableInspection();
    }

    // Check if the "3" key was just pressed this frame.
    if (Input.isKeyPressed("3")) {
        // Call the lesson 3 function — debug overlay explanation.
        lesson3_DebugOverlay();
    }

    // Check if the "4" key was just pressed this frame.
    if (Input.isKeyPressed("4")) {
        // Call the lesson 4 function — delta time and the update loop.
        lesson4_UpdateAndDeltaTime();
    }

    // Check if the "5" key was just pressed this frame.
    if (Input.isKeyPressed("5")) {
        // Call the lesson 5 function — finding actors in the scene.
        lesson5_FindingActors();
    }

    // Check if the "6" key was just pressed this frame.
    if (Input.isKeyPressed("6")) {
        // Call the lesson 6 function — component architecture.
        lesson6_WorkingWithComponents();
    }

    // Check if the "7" key was just pressed this frame.
    if (Input.isKeyPressed("7")) {
        // Call the lesson 7 function — creating and destroying actors.
        lesson7_CreateAndDestroy();
    }

    // --- Lesson 4 Active Logging ---
    // When lesson 4 is running, we log frame data at regular intervals
    // to demonstrate how onUpdate works and what delta time looks like.

    // Only run this block if the player has activated lesson 4.
    if (lesson4Active) {

        // Increment the frame counter by 1 for every frame rendered.
        frameCount = frameCount + 1;

        // Accumulate the elapsed time by adding this frame's delta time.
        lesson4Timer = lesson4Timer + dt;

        // We log every 0.5 seconds to avoid flooding the console.
        // The modulo check sees if we just crossed a 0.5-second boundary.
        if (Math.floor(lesson4Timer / 0.5) > Math.floor((lesson4Timer - dt) / 0.5)) {

            // Log the current time, frame count, and this frame's dt value.
            Debug.log("[Lesson 4] Time: " + lesson4Timer.toFixed(2) + "s | Frames: " + frameCount + " | dt: " + dt.toFixed(4) + "s");
        }

        // After 3 seconds, automatically stop the lesson 4 logging.
        // This prevents the console from filling up endlessly.
        if (lesson4Timer >= 3.0) {

            // Calculate the average FPS over the measurement period.
            var avgFps = frameCount / lesson4Timer;

            // Log the summary so the user can see real performance data.
            Debug.log("[Lesson 4] Stopped after 3 seconds.");
            Debug.log("[Lesson 4] Total frames: " + frameCount + " | Average FPS: " + avgFps.toFixed(1));

            // Disable the per-frame logging by setting the flag to false.
            lesson4Active = false;
        }
    }
}

// ============================================================================
// LESSON 1 — Console Output
// Teaches: Debug.log, Debug.warn, Debug.error, and the log() shorthand.
// The console is your most important debugging tool. Use it constantly.
// ============================================================================
function lesson1_ConsoleOutput() {

    // Print a separator so this lesson's output is easy to find in the console.
    Debug.log("────────── LESSON 1: Console Output ──────────");

    // Debug.log prints a normal message. It shows as white text in the console.
    // Use this for general information, status updates, and variable values.
    Debug.log("This is a normal log message — white text in the console");

    // Debug.warn prints a warning. It shows as yellow text in the console.
    // Use warnings for things that aren't broken but might cause issues later,
    // like a missing optional texture or a value that seems unexpectedly high.
    Debug.warn("This is a warning — yellow text, used for non-critical issues");

    // Debug.error prints an error. It shows as red text in the console.
    // Use errors for things that are genuinely broken — failed file loads,
    // null references, or states that should never happen.
    Debug.error("This is an error — red text, used for critical problems");

    // log() is a convenient shorthand for Debug.log(). It does the same thing
    // but saves you some typing during rapid debugging sessions.
    log("You can also use log() as a shorthand for Debug.log()");

    // Let the user know where they can see these messages outside the editor.
    Debug.log("");
    Debug.log("All output appears in the Console panel (View > Console).");
    Debug.log("It also appears in the terminal/command prompt if you launched from there.");
    Debug.log("Tip: Use the filter buttons in the Console to show only warnings or errors.");
    Debug.log("");
}

// ============================================================================
// LESSON 2 — Variable Inspection
// Teaches: Reading actor properties, transform data, and finding other actors.
// Every script has access to "actor" (the actor it's attached to) and
// "transform" (that actor's Transform component) as built-in variables.
// ============================================================================
function lesson2_VariableInspection() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 2: Variable Inspection ──────────");

    // "actor" is a built-in variable that refers to the actor this script is on.
    // actor.name returns the name string set in the scene editor.
    Debug.log("My actor's name: " + actor.name);

    // actor.tag returns the tag assigned to this actor in the scene editor.
    // Tags are useful for categorizing actors (e.g., "Player", "Enemy", "Pickup").
    Debug.log("My actor's tag: " + actor.tag);

    // actor.active is a boolean — true if the actor is enabled, false if disabled.
    // Disabled actors don't receive onUpdate calls or render their sprites.
    Debug.log("Am I active? " + actor.active);

    // "transform" is a built-in variable pointing to the actor's Transform component.
    // transform.x and transform.y give the actor's position in world space.
    Debug.log("My position: (" + transform.x + ", " + transform.y + ")");

    // transform.rotation is the actor's rotation in degrees (0-360).
    Debug.log("My rotation: " + transform.rotation + " degrees");

    // transform.scaleX and transform.scaleY control the actor's size multiplier.
    // A scale of 1.0 means normal size, 2.0 means double, 0.5 means half.
    Debug.log("My scale: (" + transform.scaleX + ", " + transform.scaleY + ")");

    // Now let's inspect a different actor to show we can read any actor's data.
    // Scene.find searches for an actor by its exact name string.
    var box = Scene.find("ExampleBox");

    // Always check if the result is not null before using it.
    if (box) {

        // We can read any actor's properties using dot notation.
        Debug.log("ExampleBox position: (" + box.transform.x.toFixed(1) + ", " + box.transform.y.toFixed(1) + ")");

        // Log the tag to demonstrate reading properties from other actors.
        Debug.log("ExampleBox tag: " + box.tag);
    }

    // Spacing for readability in the console.
    Debug.log("");
}

// ============================================================================
// LESSON 3 — Debug Overlay
// Teaches: The built-in F1 debug overlay for performance monitoring.
// The overlay renders directly on screen — no console needed.
// ============================================================================
function lesson3_DebugOverlay() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 3: Debug Overlay ──────────");

    // The debug overlay is a real-time HUD that shows performance information.
    // It's built into the engine and toggled with a single key.
    Debug.log("Press F1 at any time to toggle the debug overlay on/off.");

    // Blank line for readability.
    Debug.log("");

    // Explain each piece of information the overlay displays.
    Debug.log("The overlay shows:");

    // FPS (Frames Per Second) tells you how fast the game is rendering.
    // 60 FPS is the standard target for smooth gameplay.
    Debug.log("  - FPS: frames per second (target: 60 with VSync on)");

    // Delta time is the inverse of FPS — the time in seconds per frame.
    // At 60 FPS, delta time is about 0.0167 seconds (16.7 milliseconds).
    Debug.log("  - Delta Time: seconds per frame (lower is faster)");

    // Draw calls measure how many separate rendering operations the GPU performs.
    // Fewer draw calls generally means better performance.
    Debug.log("  - Draw Calls: number of rendering operations per frame");

    // Actor count tells you how many actors exist in the current scene.
    // This helps you spot accidental actor leaks (actors created but never destroyed).
    Debug.log("  - Actor Count: total actors in the active scene");

    // GC collections tracks .NET garbage collector activity.
    // Frequent collections can cause frame rate hitches.
    Debug.log("  - GC Collections: garbage collector passes (watch for spikes)");

    // Memory usage shows how much RAM the game is using.
    Debug.log("  - Memory Usage: current RAM consumption in megabytes");

    // Blank line followed by a practical tip.
    Debug.log("");
    Debug.log("Tip: Use the overlay while testing to spot performance issues early.");
    Debug.log("If FPS drops, check Actor Count and Draw Calls first.");
    Debug.log("");
}

// ============================================================================
// LESSON 4 — Update Loop & Delta Time
// Teaches: How onUpdate works, what dt means, and frame-rate independence.
// This is arguably the most important concept in game programming.
// ============================================================================
function lesson4_UpdateAndDeltaTime() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 4: Update Loop & Delta Time ──────────");

    // Enable the per-frame logging in the onUpdate function above.
    lesson4Active = true;

    // Reset the frame counter to zero so we start counting from now.
    frameCount = 0;

    // Reset the timer to zero so we measure exactly 3 seconds of data.
    lesson4Timer = 0;

    // Explain what delta time is and why every game developer needs to understand it.
    Debug.log("The engine calls onUpdate() every single frame.");

    // dt (delta time) is the key to frame-rate independent movement and logic.
    Debug.log("The 'dt' parameter is 'delta time' — seconds since the last frame.");

    // Concrete example: if you move something 100 pixels per second,
    // you multiply 100 * dt each frame. At 60 FPS, that's 100 * 0.0167 = 1.67 pixels per frame.
    Debug.log("At 60 FPS, dt is ~0.0167. At 30 FPS, dt is ~0.033.");

    // This is why we always multiply movement speed by dt.
    Debug.log("Always multiply speed by dt so movement is consistent at any frame rate.");

    // Let the user know what's about to happen.
    Debug.log("");
    Debug.log("Logging frame data every 0.5 seconds for the next 3 seconds...");
    Debug.log("");
}

// ============================================================================
// LESSON 5 — Finding Actors
// Teaches: Scene.find, Scene.findByTag, and handling null results.
// You'll use these constantly to make actors interact with each other.
// ============================================================================
function lesson5_FindingActors() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 5: Finding Actors ──────────");

    // Scene.find searches the entire scene for an actor with the exact name.
    // It returns the actor object if found, or null if no match exists.
    var box = Scene.find("ExampleBox");

    // Log whether we found it. In a real game, you'd use this reference
    // to move the actor, read its data, or call functions on its scripts.
    Debug.log("Scene.find('ExampleBox'): " + (box ? "Found! (" + box.name + ")" : "Not found"));

    // Scene.findByTag searches for actors by their tag property.
    // Tags are useful when you have many actors of the same type (e.g., "Enemy").
    var target = Scene.findFirstByTag("ClickTarget");

    // Log the result of the tag-based search.
    Debug.log("Scene.findByTag('ClickTarget'): " + (target ? "Found! (" + target.name + ")" : "Not found"));

    // Demonstrate what happens when a search fails — it returns null.
    // This is important: always check for null before accessing properties.
    var missing = Scene.find("NonExistent");

    // Log that the search returned null, which is expected and not an error.
    Debug.log("Scene.find('NonExistent'): " + (missing ? "Found" : "null (not found — this is expected!)"));

    // Explain when to use each search method.
    Debug.log("");

    // Use find() when you know the exact, unique name of the actor.
    Debug.log("Use Scene.find('name') when you know the exact actor name.");

    // Use findByTag() when you want to find actors by category.
    Debug.log("Use Scene.findByTag('tag') when searching by category (e.g., all 'Enemy' actors).");

    // Critical safety tip: always null-check before using the result.
    Debug.log("Always check for null before accessing properties on found actors!");
    Debug.log("");
}

// ============================================================================
// LESSON 6 — Working with Components
// Teaches: getComponent, reading component properties, component architecture.
// SexyBiscuit uses an actor-component model: actors are containers, components
// provide behavior. An actor with a SpriteRenderer can be seen. An actor with
// a Rigidbody2D is affected by physics. Stack components to build behavior.
// ============================================================================
function lesson6_WorkingWithComponents() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 6: Working with Components ──────────");

    // First, find the ExampleBox actor — we'll inspect its components.
    var box = Scene.find("ExampleBox");

    // Safety check: make sure the actor exists before trying to access components.
    if (!box) {
        // If the actor doesn't exist, log an error and return early.
        Debug.error("Could not find ExampleBox! Was it deleted?");
        return;
    }

    // actor.getComponent returns a reference to a component by its type name.
    // Here we get the Rigidbody2D, which controls physics simulation.
    var rb = box.getComponent("Rigidbody2D");

    // Check that the component was found before reading its properties.
    if (rb) {
        // GravityScale controls how much gravity affects this actor.
        // 0 means no gravity, 1 means normal, 2 means double gravity.
        Debug.log("Rigidbody2D.GravityScale: " + rb.GravityScale);

        // bodyType says whether the physics engine moves this actor: Dynamic bodies
        // respond to forces, Kinematic ones only move when a script moves them.
        Debug.log("Rigidbody2D.bodyType: " + rb.bodyType);
    }

    // Now get the SpriteRenderer component, which controls how the actor looks.
    var sr = box.getComponent("SpriteRenderer");

    // Check that the SpriteRenderer exists.
    if (sr) {
        // The Color property has R, G, B, A fields (0-255 each).
        // This logs the RGBA values so you can see the blue color defined in the scene.
        Debug.log("SpriteRenderer.tint: r=" + sr.tint.r + " g=" + sr.tint.g + " b=" + sr.tint.b + " a=" + sr.tint.a);
    }

    // Explain the component model to help the user understand the architecture.
    Debug.log("");
    Debug.log("SexyBiscuit uses an actor-component architecture:");
    Debug.log("  - Actors are containers that hold components.");
    Debug.log("  - Components provide specific behavior (rendering, physics, scripts).");
    Debug.log("  - Use actor.getComponent('TypeName') to access any component.");
    Debug.log("  - Common components: SpriteRenderer, Rigidbody2D, BoxCollider2D, ScriptComponent");
    Debug.log("");
}

// ============================================================================
// LESSON 7 — Creating & Destroying Actors
// Teaches: Scene.createActor, Scene.destroy, runtime actor management.
// Being able to spawn and remove actors at runtime is essential for gameplay:
// bullets, particles, enemies, pickups, and more.
// ============================================================================
function lesson7_CreateAndDestroy() {

    // Print a separator for this lesson's output.
    Debug.log("────────── LESSON 7: Creating & Destroying Actors ──────────");

    // If we have previously created actors, this second press cleans them up.
    // This toggle behavior lets the user see both creation and destruction.
    if (createdActors.length > 0) {

        // Loop through every actor we previously created and destroy them.
        for (var i = 0; i < createdActors.length; i++) {

            // Scene.destroy removes the actor from the scene entirely.
            // After this call, the actor's onDestroy function is called,
            // and it will no longer receive onUpdate calls or be rendered.
            Scene.destroy(createdActors[i]);
        }

        // Log how many actors we cleaned up so the user can verify.
        Debug.log("Cleaned up! Destroyed " + createdActors.length + " actors.");

        // Clear the array so the next press of "7" creates new actors.
        createdActors = [];

        // Tell the user they can press 7 again to create more.
        Debug.log("Press 7 again to create new actors.");
        Debug.log("");

        // Return early — we've handled the "destroy" path.
        return;
    }

    // If we get here, there are no existing actors to clean up, so we create new ones.
    // We'll create 3 actors at random positions to demonstrate runtime spawning.
    Debug.log("Creating 3 new actors at random positions...");

    // Loop 3 times to create 3 actors.
    for (var i = 0; i < 3; i++) {

        // Scene.createActor takes a name string and returns a new, empty actor.
        // The actor starts with just a Transform component at position (0, 0).
        var newActor = Scene.createActor("TutorialActor_" + i);

        // Set a random X position between 100 and 900 pixels.
        // Math.random() returns a float between 0 and 1.
        newActor.transform.x = 100 + Math.random() * 800;

        // Set a random Y position between 100 and 500 pixels.
        newActor.transform.y = 100 + Math.random() * 400;

        // Log the new actor's name and position so the user can see what was created.
        Debug.log("  Created '" + newActor.name + "' at (" + newActor.transform.x.toFixed(0) + ", " + newActor.transform.y.toFixed(0) + ")");

        // Store a reference to this actor so we can destroy it later.
        createdActors.push(newActor);
    }

    // Tell the user how many actors now exist and how to clean them up.
    Debug.log("Created " + createdActors.length + " actors. Press 7 again to destroy them.");
    Debug.log("");
}
