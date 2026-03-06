import type { CliRenderer } from "@opentui/core";
import { listDeviceSessions, getDeviceSessionSize, deleteDeviceSession } from "../../sessions/device-sessions";
import { localSessionExists } from "../../sessions/local-sessions";
import { pullSessions } from "../../sessions/sync";
import { humanBytes, sessionDateStr } from "../formatters";
import { setStatusMessage } from "../state";
import { refresh } from "../refresh";
import { showMultiselect } from "./multiselect";
import { showProgress } from "./progress";
import { showConfirm } from "./confirm";

export async function pullFlow(renderer: CliRenderer): Promise<void> {
  const allDevice = listDeviceSessions();
  const newSessions = allDevice.filter((name) => !localSessionExists(name));

  if (newSessions.length === 0) {
    setStatusMessage("All sessions already synced");
    return;
  }

  const options = newSessions.map((name) => {
    const size = getDeviceSessionSize(name);
    return {
      label: name,
      value: name,
      hint: `${sessionDateStr(name)}  ${humanBytes(size)}`,
    };
  });

  const selected = await showMultiselect(renderer, "Select sessions to pull", options);
  if (!selected || selected.length === 0) return;

  const totalSize = selected.reduce((sum, name) => sum + getDeviceSessionSize(name), 0);
  const progress = showProgress(renderer, `Pulling ${selected.length} session(s) (~${humanBytes(totalSize)})`);

  let completed = 0;
  const result = await pullSessions(selected, (p) => {
    if (p.status === "pulling") {
      progress.update(`Pulling ${p.session}...`, completed, selected.length);
    } else if (p.status === "done") {
      completed++;
      progress.update(`Pulled ${p.session}`, completed, selected.length);
    } else if (p.status === "error") {
      completed++;
      progress.update(`Failed ${p.session}: ${p.message}`, completed, selected.length);
    }
  });

  progress.done("");

  if (result.succeeded.length > 0) {
    const doDelete = await showConfirm(
      renderer,
      `Delete ${result.succeeded.length} pulled session(s) from device?`
    );

    if (doDelete) {
      let deleted = 0;
      for (const name of result.succeeded) {
        if (deleteDeviceSession(name)) deleted++;
      }
      setStatusMessage(`Pulled ${result.succeeded.length}, deleted ${deleted} from device`);
    } else {
      setStatusMessage(`Pulled ${result.succeeded.length} session(s)`);
    }
  }

  if (result.failed.length > 0) {
    setStatusMessage(`${result.failed.length} pull(s) failed`);
  }

  refresh();
}
