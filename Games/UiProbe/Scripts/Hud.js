// Every layout mode the tree has, on one screen, so a change that breaks one of
// them is visible rather than merely untested.
//
// The old probe was the same idea against the flat API: every position was a
// literal, hand-computed for a 1280x720 window, and nothing here proved anything
// about a window that was not that size. Every number below is a size or a gap;
// where things end up is layout's job, which is the point.

var picked = "nothing yet";

function onStart() {
    UI.build({
        name: "screen", layout: "column", padding: 12, gap: 12,
        children: [
            // A row that shares its width: the sidebar is fixed, the body takes the rest.
            {
                name: "top", layout: "row", gap: 12, height: 180, crossAlign: "stretch",
                children: [
                    {
                        name: "stats", layout: "column", gap: 6, padding: 10, width: 280,
                        background: "#1a1d24",
                        children: [
                            { kind: "label", text: "JAKE01", scale: 3, tint: "#e8e6e1" },
                            { kind: "label", text: "Day 1  -  the city is quiet", tint: "#9a978f" },

                            // A bar that grows to whatever the label leaves: the arithmetic
                            // hud-kit used to do by hand as `width - 20 - labelWidth - 30`.
                            {
                                layout: "row", gap: 8, crossAlign: "center",
                                children: [
                                    { kind: "label", text: "HP", tint: "#9a978f" },
                                    { name: "hp", kind: "bar", grow: 1, height: 10,
                                      value: 0.72, tint: "#c63832", background: "#2a2d34" },
                                ],
                            },
                            {
                                layout: "row", gap: 8, crossAlign: "center",
                                children: [
                                    { kind: "label", text: "STA", tint: "#9a978f" },
                                    { name: "sta", kind: "bar", grow: 1, height: 10,
                                      value: 0.45, tint: "#d8c88a", background: "#2a2d34" },
                                ],
                            },
                        ],
                    },

                    // Wrapped text, which the flat API had no way to express at all.
                    {
                        name: "prose", grow: 1, padding: 10, background: "#15171c",
                        kind: "label", wrapText: true, tint: "#9a978f",
                        text: "This paragraph wraps to whatever width the row leaves it, "
                            + "so the panel beside it can change size without anybody "
                            + "re-breaking the sentence by hand.",
                    },
                ],
            },

            // A grid, and buttons a pad or a TV remote can walk between.
            {
                name: "menu", layout: "grid", columns: 3, gap: 8, height: 120,
                children: [
                    { name: "one",   kind: "button", text: "One",   background: "#3a4050", scale: 2 },
                    { name: "two",   kind: "button", text: "Two",   background: "#3a4050", scale: 2 },
                    { name: "three", kind: "button", text: "Three", background: "#3a4050", scale: 2 },
                    { name: "four",  kind: "button", text: "Four",  background: "#3a4050", scale: 2 },
                    { name: "five",  kind: "button", text: "Five",  background: "#3a4050", scale: 2 },
                    { name: "six",   kind: "button", text: "Six",   background: "#3a4050", scale: 2 },
                ],
            },

            // Pushes the status line to the bottom whatever is above it.
            { kind: "spacer", grow: 1 },

            {
                name: "status", layout: "row", mainAlign: "spacebetween", height: 20,
                children: [
                    { name: "picked", kind: "label", text: "", tint: "#7fa650", scale: 2 },
                    { name: "mode", kind: "label", text: "", tint: "#78b4d4", scale: 2, align: "right" },
                ],
            },
        ],
    });

    // A corner badge, anchored rather than laid out: twelve pixels in from the
    // bottom-right at any window size, which is the one thing the flat API did well.
    UI.root.add({
        name: "badge", kind: "label", text: "v1.0.1", tint: "#5a5f6a",
        absolute: true, anchor: "bottomright", x: -12, y: -12,
    });

    UI.setFocus(UI.find("one"));
}

function onUpdate(dt) {
    var names = ["one", "two", "three", "four", "five", "six"];
    for (var i = 0; i < names.length; i++) {
        if (UI.find(names[i]).clicked) { picked = names[i]; }
    }

    UI.find("picked").text = "picked: " + picked;

    // Which device the player is driving with, which is what decides whether the
    // focus ring or the hover shade is the one being drawn.
    UI.find("mode").text = UI.inputMode;
}

function onDestroy() { UI.clear(); }
