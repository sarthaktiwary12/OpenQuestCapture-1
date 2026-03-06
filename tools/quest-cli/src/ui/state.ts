import type { AppState, DeviceInfo, SessionInfo, PanelFocus } from "../types";

type StateKey = keyof AppState;
type Listener = () => void;

const listeners = new Map<StateKey, Set<Listener>>();

export const state: AppState = {
  deviceInfo: null,
  deviceConnected: false,
  sessions: [],
  focusedPanel: "actions",
  overlayActive: false,
  statusMessage: null,
};

export function onChange(key: StateKey, fn: Listener): () => void {
  if (!listeners.has(key)) listeners.set(key, new Set());
  listeners.get(key)!.add(fn);
  return () => listeners.get(key)?.delete(fn);
}

function notify(key: StateKey) {
  const fns = listeners.get(key);
  if (fns) for (const fn of fns) fn();
}

export function setDeviceInfo(info: DeviceInfo | null) {
  state.deviceInfo = info;
  state.deviceConnected = info !== null;
  notify("deviceInfo");
  notify("deviceConnected");
}

export function setSessions(sessions: SessionInfo[]) {
  state.sessions = sessions;
  notify("sessions");
}

export function setFocusedPanel(panel: PanelFocus) {
  state.focusedPanel = panel;
  notify("focusedPanel");
}

export function setOverlayActive(active: boolean) {
  state.overlayActive = active;
  notify("overlayActive");
}

export function setStatusMessage(msg: string | null) {
  state.statusMessage = msg;
  notify("statusMessage");
  if (msg) {
    setTimeout(() => {
      if (state.statusMessage === msg) {
        state.statusMessage = null;
        notify("statusMessage");
      }
    }, 3000);
  }
}
