// Wild pet AI — random wander behavior
var wanderTimer = 0;
var wanderDir = { x: 1, y: 0 };
var wanderSpeed = 1.5;

function onStart() {
    pickNewDirection();
}

function onUpdate(dt) {
    wanderTimer -= dt;
    if (wanderTimer <= 0) {
        pickNewDirection();
    }
    var pos = transform.localPosition;
    transform.localPosition = {
        x: pos.x + wanderDir.x * wanderSpeed * dt,
        y: pos.y + wanderDir.y * wanderSpeed * dt
    };
}

function pickNewDirection() {
    var angle = Math.random() * Math.PI * 2;
    wanderDir = { x: Math.cos(angle), y: Math.sin(angle) };
    wanderTimer = 1.5 + Math.random() * 2.0;
}
