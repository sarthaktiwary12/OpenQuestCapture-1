import { createCliRenderer, BoxRenderable, type CliRenderer } from "@opentui/core";
import { spawn } from "bun";
import { createDevicePanel } from "./panels/device-panel";
import { createActionsPanel, type ActionId } from "./panels/actions-panel";
import { createSessionsPanel } from "./panels/sessions-panel";
import { createLogPanel, stopLogcat } from "./panels/log-panel";
import { createStatusBar } from "./panels/status-bar";
import { pullFlow } from "./overlays/pull-flow";
import { deleteFlow } from "./overlays/delete-flow";
import { apkFlow } from "./overlays/apk-flow";
import { showConfirm } from "./overlays/confirm";
import { state, setFocusedPanel, setStatusMessage } from "./state";
import { startPolling, stopPolling, manualRefresh } from "./refresh";
import { adbSync } from "../adb/adb";
import { launchApp, forceStopApp } from "../adb/package-manager";
import type { PanelFocus } from "../types";

const FOCUS_ORDER: PanelFocus[] = ["actions", "sessions", "log"];

export async function startApp(): Promise<void> {
  const renderer = await createCliRenderer({
    exitOnCtrlC: false,
    useMouse: false,
    enableMouseMovement: false,
  });

  // Root: full-screen column layout
  const root = new BoxRenderable(renderer, {
    id: "root",
    width: "100%",
    height: "100%",
    flexDirection: "column",
  });
  renderer.root.add(root);

  // Top area: two columns
  const topRow = new BoxRenderable(renderer, {
    id: "top-row",
    width: "100%",
    flexDirection: "row",
    flexGrow: 0,
  });

  // Bottom area: two columns
  const bottomRow = new BoxRenderable(renderer, {
    id: "bottom-row",
    width: "100%",
    flexDirection: "row",
    flexGrow: 1,
  });

  // Left column
  const leftCol = new BoxRenderable(renderer, {
    id: "left-col",
    width: "60%",
    flexDirection: "column",
    flexGrow: 0,
  });

  // Right column
  const rightCol = new BoxRenderable(renderer, {
    id: "right-col",
    width: "40%",
    flexDirection: "column",
    flexGrow: 0,
  });

  // Create panels
  const devicePanel = createDevicePanel(renderer);
  const { wrapper: actionsWrapper, select: actionsSelect } = createActionsPanel(renderer, handleAction);
  const sessionsPanel = createSessionsPanel(renderer, {
    onPullSession: () => pullFlow(renderer),
    onDeleteSession: () => deleteFlow(renderer),
  });
  const logPanel = createLogPanel(renderer);
  const statusBar = createStatusBar(renderer);

  // Layout: left column has device + actions, right column has sessions + log
  leftCol.add(devicePanel);
  leftCol.add(actionsWrapper);

  rightCol.add(sessionsPanel);
  rightCol.add(logPanel);

  // Use a single row for the main content area
  const mainRow = new BoxRenderable(renderer, {
    id: "main-row",
    width: "100%",
    flexDirection: "row",
    flexGrow: 1,
  });
  mainRow.add(leftCol);
  mainRow.add(rightCol);

  root.add(mainRow);
  root.add(statusBar);

  // Focus management
  actionsSelect.focus();

  function updateFocusVisuals() {
    const panel = state.focusedPanel;
    if (panel === "actions") {
      actionsSelect.focus();
    } else if (panel === "sessions") {
      sessionsPanel.focus();
    } else if (panel === "log") {
      logPanel.focus();
    }
    renderer.requestRender();
  }

  // Global keybindings
  renderer.keyInput.on("keypress", (key: any) => {
    if (state.overlayActive) return;

    // Tab / Shift+Tab: cycle focus
    if (key.name === "tab") {
      const currentIdx = FOCUS_ORDER.indexOf(state.focusedPanel);
      const nextIdx = key.shift
        ? (currentIdx - 1 + FOCUS_ORDER.length) % FOCUS_ORDER.length
        : (currentIdx + 1) % FOCUS_ORDER.length;
      setFocusedPanel(FOCUS_ORDER[nextIdx]);
      updateFocusVisuals();
      return;
    }

    // r: manual refresh
    if (key.name === "r" && state.focusedPanel !== "log") {
      manualRefresh();
      return;
    }

    // q: quit
    if (key.name === "q") {
      cleanup();
      return;
    }

    // Ctrl+C: quit
    if (key.ctrl && key.name === "c") {
      cleanup();
      return;
    }
  });

  async function handleAction(action: ActionId) {
    switch (action) {
      case "pull":
        await pullFlow(renderer);
        break;
      case "delete":
        await deleteFlow(renderer);
        break;
      case "apk":
        await apkFlow(renderer);
        break;
      case "launch":
        try {
          launchApp();
          setStatusMessage("App launched");
        } catch (e: any) {
          setStatusMessage(`Launch failed: ${e.message}`);
        }
        break;
      case "stop":
        try {
          forceStopApp();
          setStatusMessage("App stopped");
        } catch (e: any) {
          setStatusMessage(`Stop failed: ${e.message}`);
        }
        break;
      case "screenshot":
        await takeScreenshot(renderer);
        break;
      case "reboot":
        await rebootDevice(renderer);
        break;
      case "shell":
        await openShell(renderer);
        break;
    }
  }

  async function takeScreenshot(r: CliRenderer) {
    const timestamp = new Date().toISOString().replace(/[:.]/g, "-").slice(0, 19);
    const remotePath = `/sdcard/screenshot_${timestamp}.png`;
    const localPath = `screenshot_${timestamp}.png`;

    setStatusMessage("Taking screenshot...");
    try {
      adbSync(["shell", "screencap", "-p", remotePath]);
      const proc = spawn(["adb", "pull", remotePath, localPath], {
        stdout: "pipe",
        stderr: "pipe",
      });
      await proc.exited;
      adbSync(["shell", "rm", remotePath]);
      setStatusMessage(`Screenshot saved: ${localPath}`);
    } catch (e: any) {
      setStatusMessage(`Screenshot failed: ${e.message}`);
    }
  }

  async function rebootDevice(r: CliRenderer) {
    const confirmed = await showConfirm(r, "Reboot the Quest? This will disconnect ADB.");
    if (!confirmed) return;
    try {
      adbSync(["reboot"]);
      setStatusMessage("Reboot command sent");
    } catch (e: any) {
      setStatusMessage(`Reboot failed: ${e.message}`);
    }
  }

  async function openShell(r: CliRenderer) {
    r.suspend();
    const proc = spawn(["adb", "shell"], {
      stdin: "inherit",
      stdout: "inherit",
      stderr: "inherit",
    });
    await proc.exited;
    r.resume();
  }

  function cleanup() {
    stopPolling();
    stopLogcat();
    renderer.destroy();
    process.exit(0);
  }

  // Handle process exit
  process.on("exit", () => {
    stopPolling();
    stopLogcat();
  });

  process.on("SIGINT", () => {
    cleanup();
  });

  process.on("SIGTERM", () => {
    cleanup();
  });

  // Start data polling
  startPolling();
}
