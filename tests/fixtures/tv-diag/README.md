# TV diagnostics fixtures

Real command output captured from Android emulators with a debug build of the TV app 1.1.0 (3) installed
(`emulator-api34`, `emulator-api30`) or not installed (`emulator-api34-no-app`). `getprop.txt` is already reduced to the
whitelist; the api34 one has two fake forbidden props appended (`ro.serialno`, `persist.sys.wifi.ssid`) for privacy tests.
Sony TV fixtures will be added later.
