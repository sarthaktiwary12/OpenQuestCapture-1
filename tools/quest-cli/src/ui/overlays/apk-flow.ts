import type { CliRenderer } from "@opentui/core";
import { readdirSync } from "fs";
import { join, resolve } from "path";
import {
  getInstalledVersion,
  isAppInstalled,
  installApk,
  uninstallApp,
} from "../../adb/package-manager";
import { LOCAL_BUILDS_DIR, PACKAGE_NAME } from "../../constants";
import { setStatusMessage } from "../state";
import { showSelect } from "./select";
import { showConfirm } from "./confirm";
import { showProgress } from "./progress";

function findApkFiles(): string[] {
  const buildsDir = resolve(import.meta.dir, "..", "..", "..", "..", "..", LOCAL_BUILDS_DIR);
  try {
    return readdirSync(buildsDir)
      .filter((f) => f.endsWith(".apk"))
      .map((f) => join(buildsDir, f))
      .sort();
  } catch {
    return [];
  }
}

export async function apkFlow(renderer: CliRenderer): Promise<void> {
  const installed = isAppInstalled();
  const version = installed ? getInstalledVersion() : null;

  const options = [
    { label: "Install APK from Builds/", value: "install" },
    ...(installed ? [{ label: "Uninstall app", value: "uninstall" }] : []),
    { label: `Check version${version ? ` (${version})` : ""}`, value: "check" },
  ];

  const action = await showSelect(renderer, "APK Management", options);
  if (!action) return;

  if (action === "check") {
    const ver = getInstalledVersion();
    setStatusMessage(ver ? `Version: ${ver}` : "App not installed");
    return;
  }

  if (action === "uninstall") {
    const confirmed = await showConfirm(renderer, `Uninstall ${PACKAGE_NAME}?`);
    if (!confirmed) return;

    const progress = showProgress(renderer, "Uninstalling...");
    try {
      await uninstallApp();
      progress.done("");
      setStatusMessage("Uninstalled successfully");
    } catch (e: any) {
      progress.done("");
      setStatusMessage(`Uninstall failed: ${e.message}`);
    }
    return;
  }

  if (action === "install") {
    const apks = findApkFiles();
    if (apks.length === 0) {
      setStatusMessage(`No APK files found in ${LOCAL_BUILDS_DIR}/`);
      return;
    }

    const apkOptions = apks.map((path) => ({
      label: path.split("/").pop()!,
      value: path,
    }));

    const apkPath = await showSelect(renderer, "Select APK to install", apkOptions);
    if (!apkPath) return;

    const progress = showProgress(renderer, "Installing APK...");
    try {
      await installApk(apkPath);
      progress.done("");
      const newVer = getInstalledVersion();
      setStatusMessage(`Installed${newVer ? ` v${newVer}` : ""}`);
    } catch (e: any) {
      progress.done("");
      setStatusMessage(`Install failed: ${e.message}`);
    }
  }
}
