# Privacy Policy

Battery and Fans version 1.6 monitors battery and system performance locally on your Windows computer.

## Local readings

The app reads battery percentage, power state, capacity, charge/discharge rate, CPU usage, RAM usage and GPU performance counters through Windows APIs. It displays these readings locally. Normal operation makes no network requests and sends no telemetry.

## Local storage

Window position, pinning and height are stored in HKCU\Software\PinVaHieuNang. Enabling startup creates a PinVaHieuNang entry in HKCU\Software\Microsoft\Windows\CurrentVersion\Run containing the executable path. Disabling startup removes that entry.

The optional --diagnostics command writes readings to a local file at the path explicitly supplied by the caller. The application does not upload this file.

## User control

Exit the app through its tray menu. Disable startup before deleting the executable. You can delete the settings registry key and any diagnostic files you created.

## External services

GitHub hosts the project's source and build artifacts under its own privacy policy. Using GitHub or submitting a SignPath sponsorship application is separate from normal use of this desktop app.

## Contact

Report privacy questions through https://github.com/weasley1987/Battery-and-Fans/issues . Please avoid posting private diagnostic information in public issues.
