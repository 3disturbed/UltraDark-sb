// ResourceNode.js -- Harvestable resource node (tree, rock, berry bush)
// Attach to any actor tagged "Resource".
//
// Part of the survival loop: the player gathers from these nodes to
// collect wood, stone, and food for crafting and survival.
//
// The resource type is determined automatically from the actor's name:
//   "Tree"  --> wood
//   "Rock"  --> stone
//   "Berry" --> food

var resourceType  = "wood";
var durability    = 3;
var maxDurability = 3;

function onStart() {
    // Determine type from the actor name
    var name = actor.name;
    if (name.indexOf("Tree") >= 0) {
        resourceType = "wood";
        durability   = 3;
    } else if (name.indexOf("Rock") >= 0) {
        resourceType = "stone";
        durability   = 3;
    } else if (name.indexOf("Berry") >= 0) {
        resourceType = "food";
        durability   = 2;
    }
    maxDurability = durability;
}

// Called by SurvivorController when the player presses E near this node.
// Returns the resource type so the player can add it to inventory.
function harvest() {
    if (durability <= 0) { return null; }

    durability--;

    if (durability <= 0) {
        log(actor.name + " (" + resourceType + ") depleted.");
        Scene.destroyActor(actor);
    }

    return resourceType;
}

// Utility: lets other scripts query the type without harvesting.
function getType() {
    return resourceType;
}
