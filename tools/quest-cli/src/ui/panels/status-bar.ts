import { TextRenderable, type CliRenderer } from "@opentui/core";
import { state, onChange } from "../state";
import pc from "picocolors";

export function createStatusBar(renderer: CliRenderer): TextRenderable {
  const bar = new TextRenderable(renderer, {
    id: "status-bar",
    height: 1,
    width: "100%",
    content: getHints(),
  });

  function getHints(): string {
    if (state.overlayActive) {
      return pc.dim(" Esc close · Enter confirm · Space toggle · a select all");
    }

    switch (state.focusedPanel) {
      case "actions":
        return pc.dim(" Tab switch · ↑↓ navigate · Enter select · r refresh · q quit");
      case "sessions":
        return pc.dim(" Tab switch · ↑↓ navigate · p pull · d delete · r refresh · q quit");
      case "log":
        return pc.dim(" Tab switch · ↑↓ scroll · G bottom · c clear · r refresh · q quit");
      default:
        return pc.dim(" Tab switch · r refresh · q quit");
    }
  }

  function update() {
    const msg = state.statusMessage;
    bar.content = msg ? `  ${pc.bold(msg)}  ${pc.dim("|")}  ${getHints()}` : getHints();
    renderer.requestRender();
  }

  onChange("focusedPanel", update);
  onChange("overlayActive", update);
  onChange("statusMessage", update);

  return bar;
}
