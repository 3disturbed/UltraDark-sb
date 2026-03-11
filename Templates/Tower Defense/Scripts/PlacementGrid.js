// PlacementGrid.js — Tower placement system for tower defense
// Attach this script to the PlacementGrid actor in the Battlefield scene.
//
// Responsibilities:
//   1. Listen for tower type selection (keys 1, 2, 3).
//   2. On mouse click, snap to the 64px grid and validate placement.
//   3. Check that the player has enough gold and the cell is not occupied
//      or too close to the enemy path.
//   4. Create tower actors with the correct type properties.
//
// Design notes (tower defense pattern):
//   The placement grid prevents towers from blocking the enemy path.
//   Snapping to a grid makes placement predictable and prevents towers
//   from overlapping. Each tower type offers a trade-off:
//     - Basic: cheap, balanced range/damage, good all-rounder
//     - Fast:  rapid fire but lower damage per shot, good vs many weak enemies
//     - Splash: expensive, slow fire rate but high burst damage, good vs groups

// =============================================================================
// Tower type definitions
// =============================================================================
// Each tower type has a name, cost, combat stats, and a display color.
// These values are passed to Tower.js when a tower is created.

var towerTypes = {
    1: { name: "Basic",  cost: 50,  range: 120, damage: 20, cooldown: 1.0, color: { R: 50,  G: 200, B: 50,  A: 255 } },
    2: { name: "Fast",   cost: 75,  range: 100, damage: 10, cooldown: 0.4, color: { R: 50,  G: 150, B: 255, A: 255 } },
    3: { name: "Splash", cost: 100, range: 90,  damage: 35, cooldown: 1.5, color: { R: 255, G: 150, B: 50,  A: 255 } }
};

// =============================================================================
// Placement state
// =============================================================================

// Currently selected tower type (1, 2, or 3). Defaults to Basic.
var selectedTowerType = 1;

// Grid cell size in pixels. Towers snap to multiples of this value.
var gridSize = 64;

// Dictionary of occupied grid positions. Keys are "gridX,gridY" strings.
// Prevents placing two towers on the same cell.
var placedTowers = {};

// =============================================================================
// Lifecycle callbacks
// =============================================================================

function onStart() {
    log("----------------------------------------");
    log("  Tower Types:");
    log("  [1] Basic  — 50g  | Range: 120 | Dmg: 20 | Rate: 1.0s");
    log("  [2] Fast   — 75g  | Range: 100 | Dmg: 10 | Rate: 0.4s");
    log("  [3] Splash — 100g | Range: 90  | Dmg: 35 | Rate: 1.5s");
    log("----------------------------------------");
    log("  Press 1/2/3 to select, click to place.");
    log("----------------------------------------");
}

function onUpdate(dt) {
    // -- Tower type selection -------------------------------------------------
    // Number keys 1-3 switch the active tower type. The selection persists
    // until changed, so the player can place multiple towers of the same type.
    if (Input.isKeyPressed("D1") || Input.isKeyPressed("1")) {
        selectedTowerType = 1;
        log("Selected: Basic Tower (50g)");
    }
    if (Input.isKeyPressed("D2") || Input.isKeyPressed("2")) {
        selectedTowerType = 2;
        log("Selected: Fast Tower (75g)");
    }
    if (Input.isKeyPressed("D3") || Input.isKeyPressed("3")) {
        selectedTowerType = 3;
        log("Selected: Splash Tower (100g)");
    }

    // -- Tower placement on click ---------------------------------------------
    if (Input.isMouseButtonPressed(0)) {
        var mouseX = Input.mouseX;
        var mouseY = Input.mouseY;

        // Snap mouse position to the nearest grid cell center
        var gridX = Math.floor(mouseX / gridSize);
        var gridY = Math.floor(mouseY / gridSize);
        var cellCenterX = gridX * gridSize + gridSize / 2;
        var cellCenterY = gridY * gridSize + gridSize / 2;

        // -- Validate placement -----------------------------------------------
        var towerData = towerTypes[selectedTowerType];
        var manager = Scene.find("TDManager");

        // Check if cell is already occupied
        var key = gridX + "," + gridY;
        if (placedTowers[key]) {
            log("Cannot place: cell already occupied!");
            return;
        }

        // Check if position is too close to the enemy path
        if (isOnPath(cellCenterX, cellCenterY)) {
            log("Cannot place: too close to enemy path!");
            return;
        }

        // Check if the player can afford the tower
        if (!manager) return;
        var currentGold = manager.getGold();

        if (currentGold < towerData.cost) {
            log("Cannot place: not enough gold! Need " + towerData.cost + ", have " + currentGold);
            return;
        }

        // -- Place the tower --------------------------------------------------
        placeTower(gridX, gridY, cellCenterX, cellCenterY, towerData, manager);
    }
}

// =============================================================================
// Tower placement
// =============================================================================

// Creates a tower actor at the given grid position with the appropriate
// components and stats for the selected tower type.
function placeTower(gridX, gridY, worldX, worldY, towerData, manager) {
    // Deduct gold from the player's economy
    manager.spendGold(towerData.cost);

    // Mark this grid cell as occupied
    var key = gridX + "," + gridY;
    placedTowers[key] = true;

    // Create the tower actor
    var towerName = towerData.name + "Tower_" + gridX + "_" + gridY;
    var tower = Scene.createActor(towerName);

    if (tower) {
        // Position at the grid cell center
        tower.transform.x = worldX;
        tower.transform.y = worldY;
        tower.tag = "Tower";

        // Visual appearance — colored square matching the tower type
        var sprite = tower.addComponent("SpriteRenderer");
        if (sprite) {
            sprite.Color = towerData.color;
        }

        // Attach the tower combat script
        var script = tower.addComponent("ScriptComponent");
        if (script) {
            script.ScriptPath = "Scripts/Tower.js";
        }

        // Store tower stats as custom properties for Tower.js to read.
        // Tower.js reads these in its onStart to configure combat behavior.
        tower.towerRange = towerData.range;
        tower.towerDamage = towerData.damage;
        tower.towerCooldown = towerData.cooldown;
        tower.towerType = towerData.name;

        log("Placed " + towerData.name + " tower at (" + worldX + ", " + worldY + ")! Gold remaining: " + manager.getGold());
    }
}

// =============================================================================
// Path proximity check
// =============================================================================

// Returns true if the given world position is too close to any segment of
// the enemy path. This prevents towers from being placed directly on the
// path, which would either block enemies or look wrong.
//
// Uses a simplified point-to-line-segment distance check. The threshold
// is half the grid size, creating a buffer zone around the path.
function isOnPath(x, y) {
    var manager = Scene.find("TDManager");
    if (!manager) return false;

    var nodes = manager.getPathNodes();
    if (!nodes || nodes.length < 2) return false;

    var threshold = gridSize * 0.75;

    for (var i = 0; i < nodes.length - 1; i++) {
        var ax = nodes[i].x;
        var ay = nodes[i].y;
        var bx = nodes[i + 1].x;
        var by = nodes[i + 1].y;

        // Calculate the shortest distance from point (x,y) to the line
        // segment (ax,ay)-(bx,by) using projection.
        var abx = bx - ax;
        var aby = by - ay;
        var apx = x - ax;
        var apy = y - ay;

        var abLenSq = abx * abx + aby * aby;
        if (abLenSq === 0) continue;

        // Project point onto the line, clamped to [0,1] for the segment
        var t = (apx * abx + apy * aby) / abLenSq;
        if (t < 0) t = 0;
        if (t > 1) t = 1;

        // Closest point on the segment
        var closestX = ax + t * abx;
        var closestY = ay + t * aby;

        // Distance from the placement position to the closest point
        var dx = x - closestX;
        var dy = y - closestY;
        var dist = Math.sqrt(dx * dx + dy * dy);

        if (dist < threshold) {
            return true;
        }
    }

    return false;
}
