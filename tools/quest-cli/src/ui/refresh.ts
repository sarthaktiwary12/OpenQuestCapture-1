import { getDeviceInfo, getDeviceSessionCount } from "../adb/device";
import { getDeviceSerial } from "../adb/adb";
import { listDeviceSessions, getDeviceSessionSize } from "../sessions/device-sessions";
import { listLocalSessions, getLocalSessionSize } from "../sessions/local-sessions";
import { sessionDateStr } from "./formatters";
import { setDeviceInfo, setSessions, setStatusMessage } from "./state";
import type { SessionInfo } from "../types";

let refreshTimer: ReturnType<typeof setInterval> | null = null;

function buildSessionList(): SessionInfo[] {
  const deviceSessions = new Set(listDeviceSessions());
  const localSessions = new Set(listLocalSessions());
  const allNames = new Set([...deviceSessions, ...localSessions]);
  const sessions: SessionInfo[] = [];

  for (const name of allNames) {
    const onDevice = deviceSessions.has(name);
    const onLocal = localSessions.has(name);
    const location = onDevice && onLocal ? "both" : onDevice ? "device" : "local";

    let size = 0;
    if (onLocal) size = getLocalSessionSize(name);
    else if (onDevice) size = getDeviceSessionSize(name);

    sessions.push({ name, size, dateStr: sessionDateStr(name), location });
  }

  return sessions.sort((a, b) => b.name.localeCompare(a.name));
}

export function refresh() {
  try {
    const serial = getDeviceSerial();
    if (serial) {
      const info = getDeviceInfo();
      setDeviceInfo(info);
    } else {
      setDeviceInfo(null);
    }
  } catch {
    setDeviceInfo(null);
  }

  try {
    const sessions = buildSessionList();
    setSessions(sessions);
  } catch {
    setSessions([]);
  }
}

export function startPolling(intervalMs = 30000) {
  refresh();
  refreshTimer = setInterval(refresh, intervalMs);
}

export function stopPolling() {
  if (refreshTimer) {
    clearInterval(refreshTimer);
    refreshTimer = null;
  }
}

export function manualRefresh() {
  setStatusMessage("Refreshing...");
  refresh();
  setStatusMessage("Refreshed");
}
