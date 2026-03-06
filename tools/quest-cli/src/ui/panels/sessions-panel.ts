import {
  BoxRenderable,
  TextRenderable,
  ScrollBoxRenderable,
  type CliRenderer,
} from "@opentui/core";
import { syncStatusLabel, humanBytes } from "../formatters";
import { state, onChange } from "../state";
import type { SessionInfo } from "../../types";

export function createSessionsPanel(
  renderer: CliRenderer,
  opts?: {
    onPullSession?: (session: SessionInfo) => void;
    onDeleteSession?: (session: SessionInfo) => void;
  }
): BoxRenderable {
  const panel = new BoxRenderable(renderer, {
    id: "sessions-panel-wrapper",
    width: "40%",
    height: "50%",
    border: true,
    borderStyle: "rounded",
    title: " Sessions ",
    titleAlignment: "left",
    focusable: true,
    focusedBorderColor: "#00AAFF",
    flexDirection: "column",
  });

  const scrollBox = new ScrollBoxRenderable(renderer, {
    id: "sessions-scroll",
    width: "100%",
    flexGrow: 1,
    scrollY: true,
    stickyScroll: false,
    viewportCulling: true,
  });
  panel.add(scrollBox);

  let selectedIndex = 0;

  function update() {
    const children = scrollBox.getChildren();
    for (const child of children) {
      scrollBox.remove(child.id);
    }

    const sessions = state.sessions;
    if (sessions.length === 0) {
      const empty = new TextRenderable(renderer, {
        id: "sessions-empty",
        content: " No sessions found",
        fg: "#888888",
      });
      scrollBox.add(empty);
      renderer.requestRender();
      return;
    }

    selectedIndex = Math.min(selectedIndex, sessions.length - 1);

    for (let i = 0; i < sessions.length; i++) {
      const s = sessions[i];
      const status = syncStatusLabel(s.location);
      const isSelected = i === selectedIndex && state.focusedPanel === "sessions";
      const prefix = isSelected ? "> " : "  ";
      const line = new TextRenderable(renderer, {
        id: `session-${i}`,
        content: `${prefix}${s.name}  ${status}  ${humanBytes(s.size)}`,
        bg: isSelected ? "#334455" : undefined,
      });
      scrollBox.add(line);
    }
    renderer.requestRender();
  }

  onChange("sessions", update);
  onChange("focusedPanel", update);

  // Keyboard handling when focused
  renderer.keyInput.on("keypress", (key: any) => {
    if (state.focusedPanel !== "sessions" || state.overlayActive) return;

    const sessions = state.sessions;
    if (sessions.length === 0) return;

    if (key.name === "up" || key.name === "k") {
      selectedIndex = Math.max(0, selectedIndex - 1);
      update();
    } else if (key.name === "down" || key.name === "j") {
      selectedIndex = Math.min(sessions.length - 1, selectedIndex + 1);
      update();
    } else if (key.name === "p" && sessions[selectedIndex]?.location !== "local") {
      opts?.onPullSession?.(sessions[selectedIndex]);
    } else if (key.name === "d") {
      opts?.onDeleteSession?.(sessions[selectedIndex]);
    }
  });

  return panel;
}

export function getSessionsPanelSelectedIndex(): number {
  return 0; // Managed internally
}
