// -----------------------------------------------------------------------------
// UiNavigation — picks the neighbour a direction means, from resolved rectangles.
//
// The mirror of SexyBiscuit.Engine/UI/Focus/UiNavigation.cs. Deliberately a pure
// function over rectangles with no engine types in sight: it is the part of
// controller and TV-remote support most likely to feel wrong, and being pure is
// what lets both engines run the identical fixture and compare answers.
//
// The scoring is nearest-in-the-direction-of-travel first, with a doubled penalty
// for moving sideways, so a grid steps along its row before it jumps rows. Overlap
// on the perpendicular axis earns a bonus, so a properly aligned item beats one
// barely clipping the line and marginally closer -- the difference between a menu
// that feels right under a thumbstick and one that feels arbitrary.
// -----------------------------------------------------------------------------

/** Which way the player asked to move. */
export const NavDirection = Object.freeze({
    Up: 'Up',
    Down: 'Down',
    Left: 'Left',
    Right: 'Right',
});

/**
 * The numbers that decide which neighbour a direction means.
 *
 * These constants are the whole behaviour, and a number that is quietly different on
 * one engine is exactly the drift a regular expression over the other engine's source
 * cannot see. They are pinned by tests/fixtures/nav-cases.json, which both suites read.
 */
export const NAV_SETTINGS = Object.freeze({
    perpendicularPenalty: 2.0,
    centreWeight: 0.25,
    overlapBonus: 12.0,
    reachFactor: 2.0,
    epsilon: 0.5,
});

/**
 * The index of the candidate to move to, or -1 when the direction leads nowhere.
 *
 * @param {{x,y,width,height}} from        the rectangle currently focused
 * @param {Array<{x,y,width,height}>} candidates  everything that could be moved to
 * @param {string} direction               one of NavDirection
 */
export function find(from, candidates, direction, settings = NAV_SETTINGS) {
    if (!candidates || candidates.length === 0) return -1;

    const f = project(from, direction);

    let best = -1;
    let bestScore = Infinity;
    let bestCentreDelta = Infinity;

    for (let i = 0; i < candidates.length; i++) {
        const t = project(candidates[i], direction);
        if (t.width <= 0 || t.height <= 0) continue;

        const primaryGap = t.x - (f.x + f.width);

        // Ahead of us, by centre or by a clean edge. Centre-beyond-centre rather than
        // edge-beyond-edge, so a tall sidebar item beside a short one still reads as
        // being "to the right" rather than being skipped for overlapping slightly.
        const ahead = centreX(t) > centreX(f) + settings.epsilon
            || t.x >= f.x + f.width - settings.epsilon;
        if (!ahead) continue;

        const overlap = Math.max(0, Math.min(bottom(f), bottom(t)) - Math.max(f.y, t.y));
        const smaller = Math.max(1, Math.min(f.height, t.height));
        const overlapFraction = Math.max(0, Math.min(1, overlap / smaller));

        const secondaryGap = overlap > 0 ? 0 : Math.max(t.y - bottom(f), f.y - bottom(t));
        const centreDelta = Math.abs(centreY(t) - centreY(f));

        // Anything this far off to one side is not what the player meant, however close
        // it is; without this, Right can select something two rows down and half a
        // screen across.
        const reach = Math.max(f.height, t.height) * settings.reachFactor + Math.max(0, primaryGap);
        if (centreDelta > reach) continue;

        const score = Math.max(0, primaryGap)
            + secondaryGap * settings.perpendicularPenalty
            + centreDelta * settings.centreWeight
            - overlapFraction * settings.overlapBonus;

        // Ties resolve by alignment and then by the order the caller listed them, so the
        // answer is always the same one and a test can assert an exact winner.
        const better = score < bestScore - 0.0001
            || (Math.abs(score - bestScore) <= 0.0001 && centreDelta < bestCentreDelta - 0.0001);

        if (!better) continue;

        best = i;
        bestScore = score;
        bestCentreDelta = centreDelta;
    }

    return best >= 0 ? best : findInCone(f, candidates, direction, settings);
}

/**
 * The fallback for layouts with no rectangular alignment at all.
 *
 * A radial or diamond menu overlaps nothing in any direction, so the strict pass
 * rejects every candidate and the menu becomes unusable on a pad. Accepting anything
 * within 45 degrees of the direction, scored by plain distance, makes a diamond
 * navigate the way it looks and leaves a scattered layout at least moving.
 */
function findInCone(f, candidates, direction, settings) {
    let best = -1;
    let bestDistance = Infinity;
    let bestCentreDelta = Infinity;

    for (let i = 0; i < candidates.length; i++) {
        const t = project(candidates[i], direction);
        if (t.width <= 0 || t.height <= 0) continue;

        const dx = centreX(t) - centreX(f);
        const dy = centreY(t) - centreY(f);

        if (dx <= settings.epsilon) continue;
        if (Math.abs(dy) > Math.abs(dx)) continue;

        const distance = dx * dx + dy * dy;
        const centreDelta = Math.abs(dy);

        const better = distance < bestDistance - 0.0001
            || (Math.abs(distance - bestDistance) <= 0.0001 && centreDelta < bestCentreDelta);

        if (!better) continue;

        best = i;
        bestDistance = distance;
        bestCentreDelta = centreDelta;
    }

    return best;
}

/**
 * Rotates and mirrors a rectangle so that "forward" is always +X.
 *
 * Four directions written out four times is four chances to get one of them subtly
 * wrong, and the wrong one is usually Up because it is the one nobody tests by hand.
 */
function project(r, direction) {
    switch (direction) {
        case NavDirection.Right: return { x: r.x, y: r.y, width: r.width, height: r.height };
        case NavDirection.Left:  return { x: -(r.x + r.width), y: r.y, width: r.width, height: r.height };
        case NavDirection.Down:  return { x: r.y, y: r.x, width: r.height, height: r.width };
        case NavDirection.Up:    return { x: -(r.y + r.height), y: r.x, width: r.height, height: r.width };
        default: return r;
    }
}

function centreX(r) { return r.x + r.width * 0.5; }
function centreY(r) { return r.y + r.height * 0.5; }
function bottom(r) { return r.y + r.height; }
