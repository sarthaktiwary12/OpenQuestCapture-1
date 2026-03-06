import { BoxRenderable, TextRenderable, type CliRenderer } from "@opentui/core";
import { humanBytes, storageBar, batteryIndicator } from "../formatters";
import { state, onChange } from "../state";
import { PACKAGE_NAME } from "../../constants";

export function createDevicePanel(renderer: CliRenderer): BoxRenderable {
  const panel = new BoxRenderable(renderer, {
    id: "device-panel",
    width: "60%",
    height: 6,
    border: true,
    borderStyle: "rounded",
    title: " Device ",
    titleAlignment: "left",
    flexDirection: "column",
    paddingX: 1,
  });

  const content = new TextRenderable(renderer, {
    id: "device-content",
    content: "Connecting...",
  });
  panel.add(content);

  function update() {
    const info = state.deviceInfo;
    if (!info) {
      content.content = "No device connected";
      renderer.requestRender();
      return;
    }

    const usedPercent =
      info.storageTotal > 0
        ? Math.round(((info.storageTotal - info.storageFree) / info.storageTotal) * 100)
        : 0;

    const appStatus = info.appVersion ? `v${info.appVersion}` : "not installed";
    const sessionCount = state.sessions.filter(
      (s) => s.location === "device" || s.location === "both"
    ).length;

    const lines = [
      `${info.model} (${info.serial}) · Android ${info.androidVersion}`,
      `Battery  ${batteryIndicator(info.batteryLevel, info.batteryCharging)}`,
      `Storage  ${storageBar(usedPercent)}`,
      `App ${appStatus} · ${sessionCount} session${sessionCount !== 1 ? "s" : ""}`,
    ];

    content.content = lines.join("\n");
    renderer.requestRender();
  }

  onChange("deviceInfo", update);
  onChange("sessions", update);

  return panel;
}
