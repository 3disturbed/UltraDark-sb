// ATB (Active Time Battle) gauge management
var gauges = {};
var fillRates = {};

function onStart() {
    Debug.log("ATB system initialised");
}

function initCombatant(id, speed) {
    gauges[id] = 0;
    fillRates[id] = speed * 0.5; // base fill rate
}

function onUpdate(dt) {
    var readyIds = [];
    for (var id in gauges) {
        if (gauges[id] < 100) {
            gauges[id] += fillRates[id] * dt * 10;
            if (gauges[id] >= 100) {
                gauges[id] = 100;
                readyIds.push(id);
            }
        }
    }
    return readyIds;
}

function resetGauge(id) {
    gauges[id] = 0;
}

function getGauge(id) {
    return gauges[id] || 0;
}
