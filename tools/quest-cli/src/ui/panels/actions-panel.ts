import { BoxRenderable, SelectRenderable, SelectRenderableEvents, type CliRenderer } from "@opentui/core";

export type ActionId =
  | "pull"
  | "delete"
  | "apk"
  | "launch"
  | "stop"
  | "screenshot"
  | "reboot"
  | "shell";

const ACTION_OPTIONS = [
  { name: "Pull Sessions", description: "Download recordings from Quest", value: "pull" },
  { name: "Delete Sessions", description: "Remove from device or local", value: "delete" },
  { name: "Install APK", description: "Install/uninstall app builds", value: "apk" },
  { name: "Launch App", description: "Start the app on device", value: "launch" },
  { name: "Stop App", description: "Force stop the running app", value: "stop" },
  { name: "Screenshot", description: "Capture device screen", value: "screenshot" },
  { name: "Reboot", description: "Restart the device", value: "reboot" },
  { name: "ADB Shell", description: "Open interactive shell", value: "shell" },
];

export interface ActionsPanelResult {
  wrapper: BoxRenderable;
  select: SelectRenderable;
}

export function createActionsPanel(
  renderer: CliRenderer,
  onAction: (action: ActionId) => void
): ActionsPanelResult {
  const wrapper = new BoxRenderable(renderer, {
    id: "actions-wrapper",
    width: "60%",
    flexGrow: 1,
    border: true,
    borderStyle: "rounded",
    title: " Actions ",
    titleAlignment: "left",
    focusedBorderColor: "#00AAFF",
    flexDirection: "column",
  });

  const select = new SelectRenderable(renderer, {
    id: "actions-select",
    width: "100%",
    flexGrow: 1,
    options: ACTION_OPTIONS,
    wrapSelection: true,
    showDescription: true,
  });

  wrapper.add(select);

  select.on(SelectRenderableEvents.ITEM_SELECTED, (index: number, option: any) => {
    onAction(option.value as ActionId);
  });

  return { wrapper, select };
}
