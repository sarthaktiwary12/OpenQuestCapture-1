# VR Reliability — Manual Verification Script (Workstream E)

Run these on a physically-present Quest 3 before merging `fix/ws-E-vr-reliability`
to main. Nothing below can be exercised without the headset.

## 0. Prerequisites

- Quest 3 paired over USB, developer mode ON.
- `adb devices` shows the headset.
- A test APK built from this branch installed on the headset.
- A phone running the FieldData Pro app on the same LAN.

```sh
# Confirm the server is reachable on the LAN
adb shell "ip addr show wlan0 | grep 'inet '"
# Expect: inet 192.168.x.y/...
```

## 1. P0-1 — Bearer token auth

### 1a. Reject unauthenticated requests

```sh
# Should return 401 with body {"error":"unauthorized", ...}
QUEST_IP=$(adb shell ip route | awk '/wlan0/ {print $9; exit}')
curl -i "http://${QUEST_IP}:8080/api/status"
```

Expected:
```
HTTP/1.1 401 Unauthorized
{"error":"unauthorized","message":"missing or invalid bearer token"}
```

### 1b. Reject wrong bearer

```sh
curl -i -H "Authorization: Bearer WRONG" "http://${QUEST_IP}:8080/api/status"
```

Expected: `HTTP/1.1 401 Unauthorized`.

### 1c. Pair via USB (loopback-only endpoint)

```sh
adb reverse tcp:9555 tcp:8080
curl -s "http://127.0.0.1:9555/api/pair-token"
```

Expected JSON:
```json
{"token":"abc123...","header":"Authorization","scheme":"Bearer"}
```

Grab the token into a variable:
```sh
TOKEN=$(curl -s http://127.0.0.1:9555/api/pair-token | python3 -c "import sys,json; print(json.load(sys.stdin)['token'])")
```

### 1d. Accept valid bearer

```sh
curl -i -H "Authorization: Bearer ${TOKEN}" "http://${QUEST_IP}:8080/api/status"
```

Expected: `HTTP/1.1 200 OK` with device status JSON.

### 1e. Confirm HUD shows token prefix

Put the headset on — look in the upper-left of the overlay for `pair: XXXXXX…`.
The prefix must match the first 6 chars of `$TOKEN`.

### 1f. Reject loopback-only endpoint from LAN

```sh
curl -i "http://${QUEST_IP}:8080/api/pair-token"
```

Expected: `HTTP/1.1 401 Unauthorized  "exempt endpoint available on loopback only"`.

### 1g. Rotate the relay key without rebuilding

```sh
adb shell "mkdir -p /sdcard/fielddata"
adb shell "echo 'rotated-key-$(date +%s)' > /sdcard/fielddata/relay_api_key.txt"
adb shell "am force-stop com.sentient.realitylog" # replace with real package id
# Relaunch the app on the Quest. CloudRelay logs should show the new key is in use.
adb logcat -s Unity:V | grep -i "CloudRelay"
```

Delete the file and confirm we fail closed:
```sh
adb shell "rm /sdcard/fielddata/relay_api_key.txt"
# Relay requests must stop; logcat shows "relay key missing at ... skipping request"
```

## 2. P1-11 — Keep-awake watchdog read-back

### 2a. Trigger force-sleep scenario

With the app running and headset ON:
```sh
adb shell getprop debug.oculus.proximityDisabled
# Expect: 1
```

Take the headset off but leave the device powered. Within 2 seconds:
```sh
adb shell getprop debug.oculus.proximityDisabled
# Expect: still 1 (watchdog re-sets it every 2s)
```

### 2b. Simulate setprop failure

Revoke the property temporarily:
```sh
adb shell setprop debug.oculus.proximityDisabled 0
# Watchdog should re-set to 1 within 2s. Confirm:
sleep 3
adb shell getprop debug.oculus.proximityDisabled  # -> 1
```

### 2c. Simulate an uncooperative property (read-only/unstickable)

The cleanest way to verify the unhealthy path is with a log injection:
```sh
adb logcat -c
# Put the device under memory pressure or run a stress test that causes
# Runtime.exec(setprop ...) to fail. Observe:
adb logcat -s Unity:V | grep KeepAwakeBootstrap
# Expect: "proximityDisabled did not stick after 3 retries" once failing.
```

Then check the heartbeat payload reflects it:
```sh
curl -s -H "Authorization: Bearer ${TOKEN}" "http://${QUEST_IP}:8080/api/status"
# OR inspect the cloud relay heartbeat via the dashboard: keepAwakeHealthy=false.
```

## 3. P1-14 — Storage + MCAP validation

### 3a. Fill storage to simulate near-full

```sh
# Fill /sdcard to leave <500MB free
adb shell "dd if=/dev/zero of=/sdcard/pad.bin bs=1M count=50000 || true"
adb shell "df /sdcard"
```

Hit start-recording via phone or curl:
```sh
curl -i -H "Authorization: Bearer ${TOKEN}" -X POST "http://${QUEST_IP}:8080/api/start-recording" -d '{}'
```

Expected: `HTTP/1.1 507 Insufficient Storage` with `storageFreeBytes` and
`storageRequiredBytes` fields.

Clean up:
```sh
adb shell "rm /sdcard/pad.bin"
```

### 3b. Atomic guarantee (concurrent start)

```sh
for i in 1 2 3 4 5; do
  curl -s -o /tmp/out_$i -w "%{http_code}\n" -H "Authorization: Bearer ${TOKEN}" \
       -X POST "http://${QUEST_IP}:8080/api/start-recording" -d '{}' &
done
wait
```

Expected: exactly one `200`, four `409 already_recording`. No duplicated session directory on disk.

### 3c. MP4/MCAP validation — happy path

Record ~5s on the headset, then stop:
```sh
curl -s -H "Authorization: Bearer ${TOKEN}" -X POST "http://${QUEST_IP}:8080/api/stop-recording"
```

Expected: `{"status":"stopped", "corruptReason": null, ...}`.

### 3d. MP4 corruption — truncated file

While a recording is running, kill the app to simulate an ungraceful stop:
```sh
adb shell "am force-stop com.sentient.realitylog"
# Reopen the app, then re-trigger validation by issuing stop on the phantom session,
# or wait for the post-restart validation pass.
adb logcat -s Unity:V | grep "Session marked CORRUPT"
```

Expected: a `session_corrupt.txt` file in the session directory with a
`mp4:Missing...` or `mp4:Truncated` reason. Phone receives `422` if it asks for stop.

### 3e. MCAP corruption — manual

```sh
# From shell, truncate the mcap footer:
adb shell "cd /sdcard/Android/data/com.sentient.realitylog/files/<SESSION_DIR> && ls *.mcap"
# Copy off, truncate, push back, then call stop-recording (or restart app) to re-validate:
adb pull /sdcard/.../some.mcap /tmp/some.mcap
truncate -s -8 /tmp/some.mcap
adb push /tmp/some.mcap /sdcard/...
```

Then trigger `ValidateSavedSession` (restart app). Expect `mcap:...:MissingFooter`
in `session_corrupt.txt`.

## 4. NUnit (run in Unity)

Open the project in Unity Editor:
- Window → General → Test Runner
- EditMode tab → Run All
- Expected: all tests under `RealityLog.Tests` pass (AuthTokenManagerTests + SessionValidatorsTests, 20+ cases).

The validator algorithms were also independently verified on a Mac host without Unity:
```
21/21 tests passed (scripts/verify_algorithms.py — not committed; see chat log)
```

## 5. Concerns / open items requiring physical Quest

- Watchdog "setprop fails to stick" path cannot be forced reliably from a laptop; 
  the read-back logic is correct by inspection but confirming `KeepAwakeHealthy=false`
  end-to-end needs a real failure or a deliberate stubbed Runtime.
- HUD `pair:` TextMesh positioning has no parent — it renders at world origin until
  a CanvasFollow picks it up. Adjust positioning in the VR scene if the text is not
  readable at a comfortable focal distance.
- Bearer token regenerates every cold-start. The phone must re-pair after the Quest
  restarts. If this is too noisy for ops, persist the token across reboots by reading
  `pair_token.txt` at startup and only generating when the file is missing.
- The /api/pair-token endpoint relies on `adb reverse tcp:9555 tcp:8080` during pairing.
  Document this in the phone-app pairing UI before shipping.
