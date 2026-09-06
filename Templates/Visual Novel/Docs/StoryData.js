// ============================================================================
// Docs/StoryData.js — Story Authoring Reference Guide
// ============================================================================
// This file is a REFERENCE for how to author stories in the SexyBiscuit
// visual novel template. It is NOT attached to any actor in the scene.
//
// The actual story data is embedded directly in DialogueManager.js because
// each script in SexyBiscuit runs in its own isolated Jint runtime and
// cannot share variables with other scripts.
//
// To create your own stories, edit the "story" array inside
// DialogueManager.js following the patterns documented below.
// ============================================================================


// ============================================================================
// STORY NODE FORMAT
// ============================================================================
//
// A story is an array of node objects. Each node represents a single
// dialogue beat -- one speaker saying one thing. The player advances
// through nodes by pressing Space, or by selecting a choice.
//
// Required fields:
//
//   id (string)
//     A unique identifier for this node. Other nodes reference it via
//     their "next" field to create the story flow. Use snake_case names
//     that describe the narrative moment, e.g., "meet_keeper", "chose_fire".
//
//   speaker (string)
//     The name of the character speaking. This is displayed in the dialogue
//     log and used to determine which portrait to highlight.
//     Special speakers:
//       "Narrator" — Dims both portraits, text shown with * asterisks *.
//       "System"   — Used for meta-messages (credits, instructions).
//       "???"      — Unknown speaker, highlights the left portrait.
//
//   text (string)
//     The dialogue text to display. This is revealed character by character
//     via the typewriter effect. Keep lines under ~200 characters for best
//     readability in the text box.
//
// Flow fields (exactly one of these should be present):
//
//   next (string or null)
//     The id of the next node to display when the player presses Space.
//     Set to null to indicate the end of the story.
//
//   choices (array)
//     An array of choice objects (see below). When present, the
//     ChoiceManager displays the options instead of waiting for Space.
//     Do NOT include both "next" and "choices" on the same node.
//
// Optional fields:
//
//   flag (object)
//     A { key, value } pair to store in storyFlags when this node starts.
//     Useful for tracking story state without requiring a player choice.
//     Example: { key: "visited_library", value: "true" }


// ============================================================================
// CHOICE FORMAT
// ============================================================================
//
// Choices are objects inside a node's "choices" array. Each choice
// represents one option the player can select.
//
// Required fields:
//
//   text (string)
//     The label displayed to the player. Keep it concise -- ideally under
//     50 characters -- so it fits cleanly in the choice list.
//
//   next (string)
//     The id of the node to jump to when this choice is selected.
//
// Optional fields:
//
//   flag (object)
//     A { key, value } pair to store in storyFlags when the player selects
//     this choice. Use flags to track decisions and branch the story later.
//     Example: { key: "ally", value: "warrior" }


// ============================================================================
// EXAMPLE: MINIMAL STORY (3 nodes, no choices)
// ============================================================================
//
//  var story = [
//      {
//          id: "start",
//          speaker: "Narrator",
//          text: "The morning sun rises over the village.",
//          next: "greeting"
//      },
//      {
//          id: "greeting",
//          speaker: "Elder",
//          text: "Good morning, young one. Are you ready for your journey?",
//          next: "end"
//      },
//      {
//          id: "end",
//          speaker: "System",
//          text: "End of demo. Press R to restart.",
//          next: null
//      }
//  ];


// ============================================================================
// EXAMPLE: BRANCHING STORY (with choices and flags)
// ============================================================================
//
//  var story = [
//      {
//          id: "crossroads",
//          speaker: "Narrator",
//          text: "You reach a fork in the road. A signpost points in two directions.",
//          choices: [
//              { text: "Take the forest path", next: "forest", flag: { key: "path", value: "forest" } },
//              { text: "Take the mountain path", next: "mountain", flag: { key: "path", value: "mountain" } }
//          ]
//      },
//      {
//          id: "forest",
//          speaker: "Narrator",
//          text: "The trees close in around you. Birdsong fills the air.",
//          next: "destination"
//      },
//      {
//          id: "mountain",
//          speaker: "Narrator",
//          text: "The path grows steep. Snow crunches beneath your boots.",
//          next: "destination"
//      },
//      {
//          id: "destination",
//          speaker: "Guide",
//          text: "You made it! I see you chose wisely.",
//          next: null
//      }
//  ];


// ============================================================================
// STORY FLAGS — HOW THEY WORK
// ============================================================================
//
// Story flags are key-value pairs stored in the storyFlags dictionary
// inside DialogueManager.js. They let you track player decisions and
// use them later in the story.
//
// Setting a flag:
//   Flags are set automatically when a choice with a "flag" property is
//   selected, or when a node with a "flag" property starts.
//   The DialogueManager's setFlag(key, value) function handles this.
//
// Reading a flag:
//   Call getFlag(key) on the DialogueManager to retrieve a stored value.
//   Returns null if the flag has not been set.
//
// Flags are cleared when the story restarts (player presses R).
//
// Example flags from the included Chapter 1 story:
//   { key: "motivation", value: "knowledge" }  — Why the player visited
//   { key: "book",       value: "fire" }        — Which book was chosen


// ============================================================================
// TIPS FOR WRITING GOOD STORIES
// ============================================================================
//
// 1. Keep dialogue lines concise. The text box fits about 2-3 lines of text
//    comfortably. If you need more, split into multiple nodes.
//
// 2. Use the Narrator sparingly. Character dialogue feels more engaging
//    than narration. Let characters reveal the world through conversation.
//
// 3. Meaningful choices matter. Avoid choices where all options lead to the
//    same outcome. Use flags to track decisions and reference them later.
//
// 4. Test your story flow. Play through every branch to make sure all
//    "next" ids point to valid nodes. A broken link will log an error.
//
// 5. Use "???" as a speaker name for mysterious or not-yet-introduced
//    characters. It builds suspense and is visually distinct.
//
// 6. The System speaker is reserved for meta-messages like credits,
//    instructions, or chapter breaks. Don't use it for in-world dialogue.
//
// 7. Node ids should be descriptive. "chose_fire" is clearer than "node_12"
//    when you're debugging a story with dozens of branches.
