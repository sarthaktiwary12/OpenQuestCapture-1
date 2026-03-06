import {
  BoxRenderable,
  TextRenderable,
  ScrollBoxRenderable,
  type CliRenderer,
} from "@opentui/core";
import { spawnSync, type Subprocess } from "bun";
import { adbSpawn } from "../../adb/adb";
import { LOGCAT_TAGS } from "../../constants";
import { logLevelColor } from "../formatters";
import { state } from "../state";
import pc from "picocolors";

const LOG_LINE_REGEX = /^[0-9-]+\s+[0-9:.]+\s+\d+\s+\d+\s+([VDIWEF])\s+(.+)$/;
const MAX_LOG_LINES = 500;

let logProc: Subprocess | null = null;
let logLineCount = 0;

export function createLogPanel(renderer: CliRenderer): BoxRenderable {
  const panel = new BoxRenderable(renderer, {
    id: "log-panel-wrapper",
    width: "40%",
    height: "50%",
    border: true,
    borderStyle: "rounded",
    title: " Log ",
    titleAlignment: "left",
    focusable: true,
    focusedBorderColor: "#00AAFF",
    flexDirection: "column",
  });

  const scrollBox = new ScrollBoxRenderable(renderer, {
    id: "log-scroll",
    width: "100%",
    flexGrow: 1,
    scrollY: true,
    stickyScroll: true,
    stickyStart: "bottom",
    viewportCulling: true,
  });
  panel.add(scrollBox);

  function addLogLine(text: string) {
    logLineCount++;
    const line = new TextRenderable(renderer, {
      id: `log-${logLineCount}`,
      content: text,
    });
    scrollBox.add(line);

    // Trim old lines
    const children = scrollBox.getChildren();
    while (children.length > MAX_LOG_LINES) {
      const oldest = children[0];
      scrollBox.remove(oldest.id);
      children.shift();
    }

    renderer.requestRender();
  }

  function formatLogLine(raw: string): string | null {
    const match = raw.match(LOG_LINE_REGEX);
    if (match) {
      const [, level, rest] = match;
      const colorFn = logLevelColor(level);
      return colorFn(`${pc.bold(level)} ${rest}`);
    }
    const trimmed = raw.trim();
    return trimmed.length > 0 ? pc.dim(trimmed) : null;
  }

  // Start logcat
  function startLogcat() {
    try {
      spawnSync(["adb", "logcat", "-c"]);
      logProc = adbSpawn(["logcat", "-v", "threadtime", ...LOGCAT_TAGS, "*:S"], {
        onStdout: (line) => {
          const formatted = formatLogLine(line);
          if (formatted) addLogLine(formatted);
        },
        onStderr: (line) => {
          if (line.trim()) addLogLine(pc.red(line));
        },
      });
    } catch {
      addLogLine(pc.dim("Could not start logcat"));
    }
  }

  // Keyboard handling when focused
  renderer.keyInput.on("keypress", (key: any) => {
    if (state.focusedPanel !== "log" || state.overlayActive) return;

    if (key.name === "c") {
      clearLog();
    }
  });

  function clearLog() {
    const children = scrollBox.getChildren();
    for (const child of children) {
      scrollBox.remove(child.id);
    }
    logLineCount = 0;
    addLogLine(pc.dim("Log cleared"));
  }

  // Start streaming
  startLogcat();

  return panel;
}

export function stopLogcat() {
  if (logProc) {
    logProc.kill();
    logProc = null;
  }
}
