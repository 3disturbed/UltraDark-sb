// Every element kind, every anchor, real text — drawn by the shared glyph table.
function onStart() {
    UI.panel(12, 12, 268, 96, { anchor: "topleft", background: "#1a1d24" });
    UI.label(24, 22, "JAKE01", { anchor: "topleft", scale: 3, tint: "#e8e6e1" });
    UI.label(24, 50, "Day 1  -  the city is quiet", { anchor: "topleft", tint: "#9a978f" });
    UI.bar(24, 66, 244, 10, 0.72, { anchor: "topleft", tint: "#c63832", background: "#2a2d34" });
    UI.bar(24, 82, 244, 10, 0.45, { anchor: "topleft", tint: "#d8c88a", background: "#2a2d34" });

    UI.label(0, 16, "anchored top-centre", { anchor: "top", scale: 2, tint: "#7fa650", align: "center", width: 400 });
    UI.label(-12, -20, "bottom right", { anchor: "bottomright", scale: 2, tint: "#e8e6e1" });
    UI.label(12, -20, "bottom left", { anchor: "bottomleft", scale: 2, tint: "#78b4d4" });

    UI.button(0, -60, 200, 44, "PRESS ME", { anchor: "center", background: "#3a4050", tint: "#ffffff", scale: 2 });
    UI.label(0, 0, "abcdefghijklmnopqrstuvwxyz", { anchor: "center", scale: 2, tint: "#d8c88a" });
    UI.label(0, 26, "0123456789 !?.,:;()[]{}<>+-*/=", { anchor: "center", scale: 2, tint: "#d8c88a" });
    UI.label(0, 60, "ABCDEFGHIJKLMNOPQRSTUVWXYZ", { anchor: "center", scale: 2, tint: "#78b4d4" });
}
