import type { CliRenderer } from "@opentui/core";
import { listDeviceSessions, getDeviceSessionSize, deleteDeviceSession } from "../../sessions/device-sessions";
import { listLocalSessions, getLocalSessionSize, deleteLocalSession } from "../../sessions/local-sessions";
import { humanBytes, sessionDateStr } from "../formatters";
import { setStatusMessage } from "../state";
import { refresh } from "../refresh";
import { showSelect } from "./select";
import { showMultiselect } from "./multiselect";
import { showConfirm } from "./confirm";

export async function deleteFlow(renderer: CliRenderer): Promise<void> {
  const target = await showSelect(renderer, "Delete from where?", [
    { label: "Device", value: "device" },
    { label: "Local", value: "local" },
    { label: "Both", value: "both" },
  ]);

  if (!target) return;

  const includeDevice = target === "device" || target === "both";
  const includeLocal = target === "local" || target === "both";

  const deviceSessions = includeDevice ? listDeviceSessions() : [];
  const localSessions = includeLocal ? listLocalSessions() : [];
  const allNames = [...new Set([...deviceSessions, ...localSessions])].sort();

  if (allNames.length === 0) {
    setStatusMessage("No sessions found");
    return;
  }

  const options = allNames.map((name) => {
    let size = 0;
    const locations: string[] = [];
    if (deviceSessions.includes(name)) {
      size += getDeviceSessionSize(name);
      locations.push("device");
    }
    if (localSessions.includes(name)) {
      size += getLocalSessionSize(name);
      locations.push("local");
    }
    return {
      label: name,
      value: name,
      hint: `${sessionDateStr(name)}  ${humanBytes(size)}  [${locations.join("+")}]`,
    };
  });

  const selected = await showMultiselect(renderer, "Select sessions to delete", options);
  if (!selected || selected.length === 0) return;

  let spaceFreed = 0;
  for (const name of selected) {
    if (includeDevice && deviceSessions.includes(name))
      spaceFreed += getDeviceSessionSize(name);
    if (includeLocal && localSessions.includes(name))
      spaceFreed += getLocalSessionSize(name);
  }

  const confirmed = await showConfirm(
    renderer,
    `Delete ${selected.length} session(s)? (~${humanBytes(spaceFreed)} freed)`
  );

  if (!confirmed) return;

  let deletedCount = 0;
  let failedCount = 0;

  for (const name of selected) {
    let ok = true;
    if (includeDevice && deviceSessions.includes(name)) {
      ok = deleteDeviceSession(name) && ok;
    }
    if (includeLocal && localSessions.includes(name)) {
      ok = deleteLocalSession(name) && ok;
    }
    if (ok) deletedCount++;
    else failedCount++;
  }

  setStatusMessage(`Deleted ${deletedCount}, failed ${failedCount}`);
  refresh();
}
