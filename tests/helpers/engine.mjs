// The engine checkout the project sits beside, and the pieces of it the checks import.
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
export const projectRoot = path.resolve(here, "..", "..");
export const engineRoot = process.env.SB_ENGINE || path.resolve(projectRoot, "..", "..", "engine");

/** Imports a module of the engine's html5/ tree by its path under it. */
export async function engineImport(rel) {
  return import(pathToFileURL(path.join(engineRoot, "html5", rel)).href);
}
