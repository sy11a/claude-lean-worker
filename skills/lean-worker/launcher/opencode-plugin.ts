// opencode plugin the launcher installs into a worker's clean config home. It blocks every tool call once the
// launcher has written <run>/wrapup.json (spend passed the wrap-up share of the budget), so the worker's last
// message is a handoff. It also gives the worker's shell commands the user's own XDG_CONFIG_HOME back.
import { appendFileSync, existsSync, readFileSync } from "fs"
import { join } from "path"

export const LeanWorkerWrapUp = async () => {
  const runDir = process.env.LEAN_WORKER_RUN_DIR
  const original = process.env.LEAN_WORKER_XDG_CONFIG_HOME
  if (original !== undefined) {
    if (original === "") delete process.env.XDG_CONFIG_HOME
    else process.env.XDG_CONFIG_HOME = original
  }
  if (!runDir || process.env.LEAN_WORKER_WRAPUP !== "1") return {}
  const marker = join(runDir, "wrapup.json")
  return {
    "tool.execute.before": async (input: { tool: string }) => {
      appendFileSync(join(runDir, "hook.log"), `${new Date().toISOString()} ${input.tool}\n`)
      if (existsSync(marker)) throw new Error(JSON.parse(readFileSync(marker, "utf8")).reason)
    },
  }
}
