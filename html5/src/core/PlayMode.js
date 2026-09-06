// -----------------------------------------------------------------------------
// PlayMode — whether the world is running or merely being edited.
// -----------------------------------------------------------------------------

/**
 * Components still receive `awake` and `start` in edit mode; only world-changing
 * behaviour — a GameMode spawning players, a spawner emitting actors — waits on
 * this flag, so opening a scene in the editor does not populate it.
 *
 * The standalone runtime leaves it true for the life of the process.
 */
export const PlayMode = {
    isActive: true,
};
