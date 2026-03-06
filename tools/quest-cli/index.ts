#!/usr/bin/env bun
import pc from "picocolors";
import { ensureAdbAvailable, getDeviceSerial } from "./src/adb/adb";
import { startApp } from "./src/ui/app";

// Handle --help
if (process.argv.includes("--help") || process.argv.includes("-h")) {
  console.log(`
${pc.bold("OpenQuestCapture CLI")}

Interactive TUI dashboard for managing Quest recordings, device status, and ADB workflows.

${pc.dim("Usage:")}
  bun tools/quest-cli/index.ts

${pc.dim("Requirements:")}
  - ADB (Android platform-tools) in PATH
  - Quest connected via USB with developer mode enabled

${pc.dim("Navigation:")}
  Tab/Shift+Tab  Cycle panels (Actions / Sessions / Log)
  ↑↓ / j/k       Navigate within panel
  Enter           Select / confirm
  r               Refresh device info & sessions
  q               Quit
`);
  process.exit(0);
}

// Preflight: check ADB
try {
  ensureAdbAvailable();
} catch {
  console.error(pc.red("ADB not found in PATH. Install Android platform-tools first."));
  console.error(pc.dim("https://developer.android.com/tools/releases/platform-tools"));
  process.exit(1);
}

// Check device (non-fatal — app handles disconnected state)
const serial = getDeviceSerial();
if (serial) {
  console.log(pc.green(`Device connected: ${pc.dim(serial)}`));
} else {
  console.log(pc.yellow("No device detected. Dashboard will show limited info."));
}

// Launch the TUI
await startApp();
