import { BoxRenderable, TextRenderable, type CliRenderer } from "@opentui/core";
import { setOverlayActive } from "../state";
import pc from "picocolors";

export function showConfirm(
  renderer: CliRenderer,
  message: string
): Promise<boolean> {
  return new Promise((resolve) => {
    setOverlayActive(true);

    const overlay = new BoxRenderable(renderer, {
      id: "confirm-overlay",
      position: "absolute",
      width: "50%",
      height: 5,
      top: "30%",
      left: "25%",
      border: true,
      borderStyle: "rounded",
      borderColor: "#FFAA00",
      backgroundColor: "#1a1a2e",
      flexDirection: "column",
      paddingX: 1,
      paddingY: 0,
      justifyContent: "center",
      alignItems: "center",
      zIndex: 100,
    });

    const msgText = new TextRenderable(renderer, {
      id: "confirm-msg",
      content: message,
    });

    const hintText = new TextRenderable(renderer, {
      id: "confirm-hint",
      content: pc.dim("y confirm · n cancel · Esc cancel"),
    });

    overlay.add(msgText);
    overlay.add(hintText);
    renderer.root.add(overlay);
    renderer.requestRender();

    const cleanup = () => {
      renderer.root.remove("confirm-overlay");
      renderer.keyInput.removeListener("keypress", handler);
      setOverlayActive(false);
      renderer.requestRender();
    };

    const handler = (key: any) => {
      if (key.name === "y") {
        cleanup();
        resolve(true);
      } else if (key.name === "n" || key.name === "escape") {
        cleanup();
        resolve(false);
      }
    };

    renderer.keyInput.on("keypress", handler);
  });
}
