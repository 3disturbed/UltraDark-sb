// Pet capture formula
// Called from BattleManager with args: maxHp, currentHp, baseCatchRate, ballMultiplier
function calculateCaptureChance(maxHp, currentHp, baseCatchRate, ballMultiplier) {
    var hpFactor = (3 * maxHp - 2 * currentHp) / (3 * maxHp);
    var chance = hpFactor * baseCatchRate * ballMultiplier;
    return Math.min(chance, 0.95); // cap at 95%
}

function rollCapture(maxHp, currentHp, baseCatchRate, ballMultiplier) {
    var chance = calculateCaptureChance(maxHp, currentHp, baseCatchRate, ballMultiplier);
    var roll = Math.random();
    Debug.log("Capture roll: " + roll.toFixed(3) + " vs chance: " + chance.toFixed(3));
    return roll < chance;
}
