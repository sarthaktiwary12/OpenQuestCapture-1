import { BoxRenderable, TextRenderable, type CliRenderer } from "@opentui/core";
import { setOverlayActive } from "../state";
import pc from "picocolors";

export interface ProgressHandle {
  update(message: string, current?: number, total?: number): void;
  done(message: string): void;
}

export function showProgress(
  renderer: CliRenderer,
  title: string
): ProgressHandle {
  setOverlayActive(true);

  const overlay = new BoxRenderable(renderer, {
    id: "progress-overlay",
    position: "absolute",
    width: "50%",
    height: 5,
    top: "30%",
    left: "25%",
    border: true,
    borderStyle: "rounded",
    borderColor: "#00AAFF",
    backgroundColor: "#1a1a2e",
    flexDirection: "column",
    paddingX: 1,
    justifyContent: "center",
    alignItems: "center",
    zIndex: 100,
  });

  const titleText = new TextRenderable(renderer, {
    id: "progress-title",
    content: pc.bold(title),
  });

  const msgText = new TextRenderable(renderer, {
    id: "progress-msg",
    content: "Starting...",
  });

  const barText = new TextRenderable(renderer, {
    id: "progress-bar",
    content: "",
  });

  overlay.add(titleText);
  overlay.add(msgText);
  overlay.add(barText);
  renderer.root.add(overlay);
  renderer.requestRender();

  return {
    update(message: string, current?: number, total?: number) {
      msgText.content = message;
      if (current !== undefined && total !== undefined && total > 0) {
        const pct = Math.round((current / total) * 100);
        const width = 20;
        const filled = Math.round((current / total) * width);
        const bar = "█".repeat(filled) + "░".repeat(width - filled);
        barText.content = `${pc.cyan(bar)} ${pct}% (${current}/${total})`;
      }
      renderer.requestRender();
    },

    done(message: string) {
      renderer.root.remove("progress-overlay");
      setOverlayActive(false);
      renderer.requestRender();
    },
  };
}
