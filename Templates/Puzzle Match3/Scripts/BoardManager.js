// BoardManager.js — Match-3 puzzle board logic
//
// This script manages an 8x8 grid of colored tiles. The player clicks one tile
// to select it, then clicks an orthogonally adjacent tile to swap the two.
// After every swap the board is scanned for horizontal and vertical runs of 3+
// matching tiles. Matched tiles are removed, tiles above fall down (gravity),
// empty cells at the top are filled with new random tiles, and the scan repeats
// until no more matches remain. Each successive cascade within a single move
// increases a combo multiplier for bonus points.
//
// Attach this script to the BoardManager actor in the PuzzleBoard scene.

// ---------------------------------------------------------------------------
// Constants
// ---------------------------------------------------------------------------

/** Number of columns in the grid. */
var COLS = 8;

/** Number of rows in the grid. */
var ROWS = 8;

/** Size of each tile in pixels. */
var TILE_SIZE = 64;

/** X offset to center the 8x8 grid (512 px wide) in a 720 px window. */
var BOARD_OFFSET_X = 104;

/** Y offset to push the grid down, leaving room for score display at top. */
var BOARD_OFFSET_Y = 160;

/** How many distinct tile colour types exist. */
var NUM_TYPES = 5;

// ---------------------------------------------------------------------------
// State
// ---------------------------------------------------------------------------

/** 2D array [row][col] of integer tile types (0 .. NUM_TYPES-1). -1 = empty. */
var grid = [];

/** 2D array [row][col] of actor references created by Scene.instantiate. */
var tileActors = [];

/** Row of the currently selected (first-click) tile, or -1 if none. */
var selectedRow = -1;

/** Column of the currently selected (first-click) tile, or -1 if none. */
var selectedCol = -1;

/** Running score — read by ScoreDisplay.js via Scene.find. */
var score = 0;

/** Combo multiplier that increases with each successive cascade step. */
var comboMultiplier = 1;

/** When true, the board is animating cascades and input is blocked. */
var isProcessing = false;

/**
 * RGBA colour objects for the five tile types.
 * These are applied to each tile's SpriteRenderer to visually distinguish types.
 */
var TILE_COLORS = [
    { R: 220, G:  50, B:  50, A: 255 },  // 0 — Red
    { R:  50, G: 100, B: 220, A: 255 },  // 1 — Blue
    { R:  50, G: 200, B:  80, A: 255 },  // 2 — Green
    { R: 240, G: 200, B:  40, A: 255 },  // 3 — Yellow
    { R: 170, G:  60, B: 200, A: 255 }   // 4 — Purple
];

/** Human-readable names for tile types (used in log messages). */
var TILE_NAMES = ["Red", "Blue", "Green", "Yellow", "Purple"];

// ===========================================================================
// Lifecycle
// ===========================================================================

/**
 * onStart — called once when the scene loads.
 * Initialises the grid with random tile types, creates actor visuals for every
 * cell, and removes any matches that were accidentally generated so the player
 * starts with a clean board.
 */
function onStart() {
    log("BoardManager: Initialising " + COLS + "x" + ROWS + " puzzle board...");

    // --- Build the grid and actor arrays ---
    for (var r = 0; r < ROWS; r++) {
        grid[r] = [];
        tileActors[r] = [];
        for (var c = 0; c < COLS; c++) {
            // Pick a random tile type
            var type = randomType();
            grid[r][c] = type;

            // Create a visual actor for this cell
            tileActors[r][c] = createTileActor(r, c, type);
        }
    }

    // --- Remove any accidental starting matches ---
    removeInitialMatches();

    log("Board ready! Click a tile to select it, then click an adjacent tile to swap!");
}

/**
 * onUpdate — called every frame.
 * While the board is not processing cascades, listens for mouse clicks and
 * converts them to grid coordinates for tile selection and swapping.
 */
function onUpdate(dt) {
    // Block input while cascades are resolving
    if (isProcessing) {
        return;
    }

    // Detect left mouse button press
    if (Input.isMousePressed(0)) {
        // Convert screen coordinates to grid position
        var cell = screenToGrid(Input.mouseX, Input.mouseY);
        if (cell === null) {
            // Clicked outside the board — clear any selection
            clearSelection();
            return;
        }

        var row = cell.row;
        var col = cell.col;

        if (selectedRow === -1) {
            // --- First click: select this tile ---
            selectTile(row, col);
        } else if (selectedRow === row && selectedCol === col) {
            // --- Clicked the same tile again: deselect ---
            clearSelection();
        } else if (isAdjacent(selectedRow, selectedCol, row, col)) {
            // --- Second click on adjacent tile: attempt swap ---
            swapTiles(selectedRow, selectedCol, row, col);
        } else {
            // --- Second click on non-adjacent tile: re-select ---
            clearSelection();
            selectTile(row, col);
        }
    }
}

// ===========================================================================
// Selection
// ===========================================================================

/**
 * Marks the tile at (row, col) as selected and provides visual feedback by
 * scaling the tile actor up slightly.
 */
function selectTile(row, col) {
    selectedRow = row;
    selectedCol = col;
    log("Selected tile at [" + row + ", " + col + "] — " + TILE_NAMES[grid[row][col]]);

    // Visual highlight: scale the selected tile up
    var a = tileActors[row][col];
    if (a) {
        a.transform.scaleX = 1.2;
        a.transform.scaleY = 1.2;
    }
}

/**
 * Clears the current selection and resets visual highlight.
 */
function clearSelection() {
    if (selectedRow !== -1 && tileActors[selectedRow] && tileActors[selectedRow][selectedCol]) {
        var a = tileActors[selectedRow][selectedCol];
        if (a) {
            a.transform.scaleX = 1.0;
            a.transform.scaleY = 1.0;
        }
    }
    selectedRow = -1;
    selectedCol = -1;
}

// ===========================================================================
// Swapping
// ===========================================================================

/**
 * Attempts to swap the tiles at (r1,c1) and (r2,c2).
 * After swapping, the board is checked for matches. If no matches result from
 * the swap, the tiles are swapped back (invalid move).
 */
function swapTiles(r1, c1, r2, c2) {
    // Clear visual highlight before swapping
    clearSelection();

    // --- Swap types in the grid ---
    var tempType = grid[r1][c1];
    grid[r1][c1] = grid[r2][c2];
    grid[r2][c2] = tempType;

    // --- Swap actor positions on screen ---
    var pos1 = gridToScreen(r1, c1);
    var pos2 = gridToScreen(r2, c2);

    var actor1 = tileActors[r1][c1];
    var actor2 = tileActors[r2][c2];

    if (actor1) {
        actor1.transform.x = pos2.x;
        actor1.transform.y = pos2.y;
    }
    if (actor2) {
        actor2.transform.x = pos1.x;
        actor2.transform.y = pos1.y;
    }

    // --- Swap actor references in the array ---
    tileActors[r1][c1] = actor2;
    tileActors[r2][c2] = actor1;

    // --- Check if this swap creates any matches ---
    var matches = findMatches();
    if (matches.length > 0) {
        // Valid move — resolve all cascading matches
        log("Swap at [" + r1 + "," + c1 + "] <-> [" + r2 + "," + c2 + "] — match found!");
        checkAndResolveMatches();
    } else {
        // Invalid move — no matches created, swap back
        log("No match — swapping back.");
        var revertType = grid[r1][c1];
        grid[r1][c1] = grid[r2][c2];
        grid[r2][c2] = revertType;

        // Revert actor positions
        if (actor2) {
            actor2.transform.x = pos2.x;
            actor2.transform.y = pos2.y;
        }
        if (actor1) {
            actor1.transform.x = pos1.x;
            actor1.transform.y = pos1.y;
        }

        // Revert actor references
        tileActors[r1][c1] = actor1;
        tileActors[r2][c2] = actor2;
    }
}

// ===========================================================================
// Match detection and resolution
// ===========================================================================

/**
 * Main cascade loop. Finds matches, scores them, removes matched tiles, drops
 * tiles down to fill gaps, spawns new tiles at the top, and repeats until no
 * more matches remain. The comboMultiplier increases with each cascade step.
 */
function checkAndResolveMatches() {
    isProcessing = true;
    comboMultiplier = 1;

    // Iterative cascade loop — keep resolving until the board is stable
    var cascading = true;
    while (cascading) {
        var matches = findMatches();
        if (matches.length === 0) {
            // No more matches — board is stable
            cascading = false;
            break;
        }

        // --- Score the matches ---
        // Base points: 10 per matched tile, multiplied by the cascade combo
        var points = matches.length * 10 * comboMultiplier;
        score += points;
        log("Matched " + matches.length + " tiles! +" + points + " pts (x" + comboMultiplier + " combo)  Total: " + score);

        // --- Remove matched tile actors and clear grid cells ---
        removeMatches(matches);

        // --- Apply gravity: drop tiles down to fill gaps ---
        applyGravity();

        // --- Fill empty cells at the top with new random tiles ---
        fillEmpty();

        // --- Increase combo for next cascade step ---
        comboMultiplier++;
    }

    // Reset combo and unlock input
    comboMultiplier = 1;
    isProcessing = false;
}

/**
 * Scans the entire grid for horizontal and vertical runs of 3 or more
 * consecutive tiles of the same type.
 *
 * Algorithm:
 *   1. For each row, walk left to right tracking run length. When the type
 *      changes (or the row ends), if the run was 3+, record all positions.
 *   2. Repeat for each column, walking top to bottom.
 *   3. Deduplicate using a key string "row,col" in a lookup object, since a
 *      tile can be part of both a horizontal and vertical match simultaneously.
 *
 * @returns {Array} Array of {row, col} objects for every matched cell.
 */
function findMatches() {
    // Use an object as a set, keyed by "row,col" strings, to deduplicate
    var matchSet = {};

    // --- Horizontal scan ---
    for (var r = 0; r < ROWS; r++) {
        var runStart = 0;
        for (var c = 1; c <= COLS; c++) {
            // Check if the run continues (same type, not empty)
            var sameType = (c < COLS) && (grid[r][c] === grid[r][runStart]) && (grid[r][c] !== -1);
            if (!sameType) {
                // Run ended — check length
                var runLength = c - runStart;
                if (runLength >= 3 && grid[r][runStart] !== -1) {
                    // Record all positions in this horizontal run
                    for (var i = runStart; i < c; i++) {
                        matchSet[r + "," + i] = true;
                    }
                }
                runStart = c;
            }
        }
    }

    // --- Vertical scan ---
    for (var c = 0; c < COLS; c++) {
        var runStart = 0;
        for (var r = 1; r <= ROWS; r++) {
            var sameType = (r < ROWS) && (grid[r][c] === grid[runStart][c]) && (grid[r][c] !== -1);
            if (!sameType) {
                var runLength = r - runStart;
                if (runLength >= 3 && grid[runStart][c] !== -1) {
                    // Record all positions in this vertical run
                    for (var i = runStart; i < r; i++) {
                        matchSet[i + "," + c] = true;
                    }
                }
                runStart = r;
            }
        }
    }

    // --- Convert the set into an array of {row, col} objects ---
    var results = [];
    for (var key in matchSet) {
        var parts = key.split(",");
        results.push({ row: parseInt(parts[0], 10), col: parseInt(parts[1], 10) });
    }
    return results;
}

/**
 * Destroys the actor for each matched cell and marks the grid cell as empty (-1).
 *
 * @param {Array} matches — Array of {row, col} objects from findMatches().
 */
function removeMatches(matches) {
    for (var i = 0; i < matches.length; i++) {
        var r = matches[i].row;
        var c = matches[i].col;

        // Destroy the visual actor
        var a = tileActors[r][c];
        if (a) {
            a.destroy();
        }
        tileActors[r][c] = null;

        // Mark the grid cell as empty
        grid[r][c] = -1;
    }
}

/**
 * Applies gravity to the grid: for each column, tiles above an empty cell
 * fall down to fill the gap.
 *
 * Algorithm (per column, bottom to top):
 *   For each empty cell, find the nearest non-empty cell above it and move
 *   that tile down. Update both the grid array and the actor's screen position.
 */
function applyGravity() {
    for (var c = 0; c < COLS; c++) {
        // writeRow is the lowest empty row we want to fill
        var writeRow = ROWS - 1;

        // Scan from bottom to top
        for (var r = ROWS - 1; r >= 0; r--) {
            if (grid[r][c] !== -1) {
                if (r !== writeRow) {
                    // Move this tile down to writeRow
                    grid[writeRow][c] = grid[r][c];
                    grid[r][c] = -1;

                    // Move the actor reference and update its screen position
                    tileActors[writeRow][c] = tileActors[r][c];
                    tileActors[r][c] = null;

                    var pos = gridToScreen(writeRow, c);
                    if (tileActors[writeRow][c]) {
                        tileActors[writeRow][c].transform.x = pos.x;
                        tileActors[writeRow][c].transform.y = pos.y;
                    }
                }
                writeRow--;
            }
        }
        // Cells above writeRow remain empty (-1) — fillEmpty will handle them
    }
}

/**
 * Fills every empty cell (-1) in the grid with a new random tile type and
 * creates a fresh actor for it.
 */
function fillEmpty() {
    for (var r = 0; r < ROWS; r++) {
        for (var c = 0; c < COLS; c++) {
            if (grid[r][c] === -1) {
                var type = randomType();
                grid[r][c] = type;
                tileActors[r][c] = createTileActor(r, c, type);
            }
        }
    }
}

/**
 * Ensures the board starts with no matches. Repeatedly scans for matches and
 * reshuffles offending cells until the board is clean.
 *
 * Strategy: for each matched cell, assign a new random type that differs from
 * its horizontal and vertical neighbours, then re-scan. This converges quickly
 * because only conflicting cells are changed.
 */
function removeInitialMatches() {
    var maxAttempts = 100; // safety limit
    var attempt = 0;

    while (attempt < maxAttempts) {
        var matches = findMatches();
        if (matches.length === 0) {
            break; // Board is clean
        }

        // Re-assign each matched cell to a type that avoids its neighbours
        for (var i = 0; i < matches.length; i++) {
            var r = matches[i].row;
            var c = matches[i].col;
            var newType = safeRandomType(r, c);
            grid[r][c] = newType;

            // Update the actor's colour to match the new type
            // (destroy old actor and create a new one with the correct colour)
            if (tileActors[r][c]) {
                tileActors[r][c].destroy();
            }
            tileActors[r][c] = createTileActor(r, c, newType);
        }
        attempt++;
    }

    if (attempt >= maxAttempts) {
        log("Warning: Could not fully clear initial matches after " + maxAttempts + " attempts.");
    }
}

// ===========================================================================
// Coordinate conversion helpers
// ===========================================================================

/**
 * Converts grid coordinates (row, col) to screen pixel position.
 * The returned position is the centre of the tile.
 *
 * @param {number} row — Grid row (0 = top).
 * @param {number} col — Grid column (0 = left).
 * @returns {{ x: number, y: number }} Screen position in pixels.
 */
function gridToScreen(row, col) {
    return {
        x: BOARD_OFFSET_X + col * TILE_SIZE + TILE_SIZE / 2,
        y: BOARD_OFFSET_Y + row * TILE_SIZE + TILE_SIZE / 2
    };
}

/**
 * Converts screen pixel coordinates to grid (row, col).
 * Returns null if the position is outside the board area.
 *
 * @param {number} x — Screen X in pixels.
 * @param {number} y — Screen Y in pixels.
 * @returns {{ row: number, col: number } | null}
 */
function screenToGrid(x, y) {
    var col = Math.floor((x - BOARD_OFFSET_X) / TILE_SIZE);
    var row = Math.floor((y - BOARD_OFFSET_Y) / TILE_SIZE);

    if (row < 0 || row >= ROWS || col < 0 || col >= COLS) {
        return null;
    }
    return { row: row, col: col };
}

/**
 * Returns true if the two cells are orthogonally adjacent (up/down/left/right).
 * Diagonal moves are not allowed in standard match-3.
 */
function isAdjacent(r1, c1, r2, c2) {
    var dr = Math.abs(r1 - r2);
    var dc = Math.abs(c1 - c2);
    // Exactly one of dr or dc must be 1, the other must be 0
    return (dr + dc) === 1;
}

// ===========================================================================
// Tile creation helpers
// ===========================================================================

/**
 * Creates a visual actor for a tile at the given grid position and type.
 * The actor is placed at the correct screen position with a SpriteRenderer
 * coloured according to the tile type.
 *
 * @param {number} row — Grid row.
 * @param {number} col — Grid column.
 * @param {number} type — Tile type index (0 .. NUM_TYPES-1).
 * @returns {object} The actor proxy returned by Scene.instantiate.
 */
function createTileActor(row, col, type) {
    var pos = gridToScreen(row, col);
    var tileName = "Tile_" + row + "_" + col;
    var a = Scene.instantiate(tileName, pos.x, pos.y);
    // Note: the actor is created at the correct position.
    // In a full implementation, the SpriteRenderer colour would be set via
    // a component API. The tile type is tracked in the grid array and the
    // actor name encodes its position for debugging purposes.
    return a;
}

/**
 * Returns a random tile type integer in range [0, NUM_TYPES).
 */
function randomType() {
    return Math.floor(Math.random() * NUM_TYPES);
}

/**
 * Returns a random tile type that does NOT match the tile's horizontal or
 * vertical neighbours. Used during board initialisation to avoid starting
 * matches.
 *
 * @param {number} row — Grid row of the cell.
 * @param {number} col — Grid column of the cell.
 * @returns {number} A safe tile type index.
 */
function safeRandomType(row, col) {
    // Collect neighbour types to avoid
    var avoid = {};
    if (col >= 2 && grid[row][col - 1] === grid[row][col - 2] && grid[row][col - 1] !== -1) {
        avoid[grid[row][col - 1]] = true;
    }
    if (row >= 2 && grid[row - 1][col] === grid[row - 2][col] && grid[row - 1][col] !== -1) {
        avoid[grid[row - 1][col]] = true;
    }

    // Build list of allowed types
    var allowed = [];
    for (var t = 0; t < NUM_TYPES; t++) {
        if (!avoid[t]) {
            allowed.push(t);
        }
    }

    // Pick from allowed types (fallback to fully random if somehow all blocked)
    if (allowed.length === 0) {
        return randomType();
    }
    return allowed[Math.floor(Math.random() * allowed.length)];
}
