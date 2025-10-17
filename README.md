# APRS Server & Forwarder

- **`aprs` (Server):** An APRS-IS compatible TCP server with optional HTTP map UI, client filtering, and simple passcode validation.
- **`aprsgateway` (Forwarder):** Bridges one APRS source to another and (optionally) forwards **Meshtastic MQTT** position/telemetry into APRS.

---

## ✨ Features

**Server (`aprs`)**
- APRS-IS style TCP listener (default **14580**)
- Optional HTTP server with a simple **Leaflet** map UI (default **8060**)
- Per-client APRS filter support (enable/disable)
- Pass-back control (echo back to sender or not)
- Simple passcode check (optional)
- Allow/Deny lists for **callsigns, IPs, and MACs**
- In-memory store of recent GPS “buddies” for the map view
- Console logging toggles for config, connections, packets, broadcasts

**Forwarder (`aprsgateway`)**
- Forwards from an **upstream APRS server** to your local APRS server (or any target)
- Optional **Meshtastic MQTT → APRS** bridge:
  - Connect via MQTT (TLS optional)
  - Map Meshtastic nodes to APRS callsigns/symbols
  - Add comment suffixes, keepalives, etc.

---

## 📁 Repository Layout

```
APRSServer.cs          // Server core
HttpAPRSServer.cs      // Built-in HTTP + map UI
APRSData.cs            // APRS parsing utilities
ClientAPRSFilter.cs    // Per-client filter logic
SimpleServersPBAuth.cs // Simple passcode auth

APRSGateWay.cs         // Forwarder core
MeshMqttBridge.cs      // Meshtastic MQTT → APRS bridge
IniFile.cs             // Windows INI helper for gateway

Program.cs             // Entry points
*.csproj               // Build projects

data/
  aprs.exe             // Prebuilt server (example binary)
  aprs.xml             // Server configuration (XML)
  aprsgateway.exe      // Prebuilt forwarder (example binary)
  aprsgateway.ini      // Forwarder configuration (INI)
  map/                 // Leaflet web UI assets
```

---

## 🚀 Quick Start (no build)

1. **Server**
   - Copy `data/aprs.xml` next to the server binary (`aprs.exe`).
   - Edit `aprs.xml` to your needs (ports, filters, HTTP server).
   - Run:
     - **Windows:** `aprs.exe`
     - **Linux (Mono):** `mono aprs.exe`
   - (Optional) Open the map UI: `http://<server-ip>:8060/` (if HTTPServer enabled).

2. **Forwarder**
   - Copy `data/aprsgateway.ini` next to the forwarder binary (`aprsgateway.exe`).
   - Edit `aprsgateway.ini` with your upstream/downstream server details and (optional) MQTT info.
   - Run:
     - **Windows:** `aprsgateway.exe`
     - **Linux (Mono):** `mono aprsgateway.exe`

Type `exit` in the console to stop either app.

---

## 🛠️ Build from Source

### Prereqs
- **Windows:** Visual Studio (or Build Tools) with .NET Framework support.
- **Linux/macOS:** **Mono** + **msbuild**.
- Projects target legacy .NET (e.g., v2.0 in the csproj). Modern toolchains generally build them fine; if needed, retarget to a newer framework.

### Commands

**Windows (Developer Command Prompt):**
```bat
msbuild APRSServer.csproj /p:Configuration=Release
msbuild APRSGateWay.csproj /p:Configuration=Release
```

**Linux/macOS (Mono):**
```bash
msbuild APRSServer.csproj /p:Configuration=Release
msbuild APRSGateWay.csproj /p:Configuration=Release
```

The resulting binaries will appear under `bin/Release/` of each project.

---

## ⚙️ Configuration

### Server (`aprs.xml`)

```xml
<?xml version="1.0" encoding="UTF-8"?>
<config>
  <!-- Main -->
  <ServerName>APRS-Server</ServerName>
  <ServerPort>14580</ServerPort>     <!-- TCP listener -->
  <MaxClients>1024</MaxClients>

  <!-- HTTP map/UI -->
  <HTTPServer>8060</HTTPServer>      <!-- 0 = disabled -->

  <!-- Outgoing behavior -->
  <EnableClientFilter>1</EnableClientFilter>   <!-- 0/1 -->
  <PassBackAPRSPackets>1</PassBackAPRSPackets> <!-- 0/1 -->

  <!-- Incoming policy -->
  <OnlyValidPasswordUsers>0</OnlyValidPasswordUsers> <!-- 0/1 -->

  <!-- Filters (examples) -->
  <ListenCallMode>0</ListenCallMode>           <!-- 0: none, 1: whitelist, 2: blacklist -->
  <ListenCallAllow>*</ListenCallAllow>
  <ListenCallDeny></ListenCallDeny>

  <ListenIPMode>0</ListenIPMode>               <!-- 0/1/2 -->
  <ListenIPAllow>127.0.0.1;192.168.*.*</ListenIPAllow>
  <ListenIPDeny></ListenIPDeny>

  <ListenMacMode>0</ListenMacMode>             <!-- 0/1/2 -->
  <ListenMacAllow>*-*-*-*-*-*</ListenMacAllow>
  <ListenMacDeny></ListenMacDeny>

  <!-- Console output -->
  <OutConfigToConsole>0</OutConfigToConsole>
  <OutAPRStoConsole>0</OutAPRStoConsole>
  <OutConnectionsToConsole>0</OutConnectionsToConsole>
  <OutBroadcastsMessages>0</OutBroadcastsMessages>
  <OutBuddiesCount>0</OutBuddiesCount>
</config>
```

### Forwarder (`aprsgateway.ini`)

```ini
[APRSGateway]
server_in_url=example.upstream.net
server_in_port=14580
server_out_url=127.0.0.1
server_out_port=14580
callsign=N0CALL
passcode=XXXXX

[MeshGateway]
enabled=false
mqtt_host=mqtt.example.net
mqtt_port=1883
mqtt_user=youruser
mqtt_pass=yourpass
mqtt_topic=msh/REGION/BAND/json/LongFast/#
use_tls=false
tls_ignore_errors=true
keepalive_secs=60
node_callsign_prefix=MT0CALL
default_symbol=\G
comment_suffix=via Meshtastic
```

- **`APRSGateway` section**
  - `server_in_*`: Where to read APRS from (e.g., an upstream APRS-IS).
  - `server_out_*`: Where to write APRS to (e.g., your local `aprs` server).
  - `callsign` / `passcode`: Your gateway’s identity/passcode.

- **`MeshGateway` section (optional)**
  - Connects to a Meshtastic MQTT broker and converts node updates to APRS packets.
  - Control callsign prefixing, APRS symbol, and appended comments.

---

## 🔌 Ports & Firewall

- APRS TCP: **14580** (configurable)
- HTTP UI: **8060** (configurable; set `0` to disable)
- MQTT: as configured in `aprsgateway.ini` (if using bridge)

Open/forward only what you need. If the server is public, use allowlists.

---

## 🧪 Operating Tips

- **Stopping:** Type `exit` in the console.
- **Logging:** Use the `Out*` switches in `aprs.xml` to tune verbosity.
- **Filters:** Start permissive, then tighten with allowlists (callsigns/IPs/MACs).
- **Map UI:** Useful for quick visual checks of recent packets; not intended as a full APRS client.

---

## 🐧 Systemd Example (optional)

```ini
# /etc/systemd/system/aprs.service
[Unit]
Description=APRS Server
After=network-online.target

[Service]
WorkingDirectory=/opt/aprs
ExecStart=/usr/bin/mono /opt/aprs/aprs.exe
Restart=on-failure
User=aprs

[Install]
WantedBy=multi-user.target
```

```ini
# /etc/systemd/system/aprsgateway.service
[Unit]
Description=APRS Forwarder
After=network-online.target

[Service]
WorkingDirectory=/opt/aprsgateway
ExecStart=/usr/bin/mono /opt/aprsgateway/aprsgateway.exe
Restart=on-failure
User=aprs

[Install]
WantedBy=multi-user.target
```

---

## 🙏 Acknowledgements

- APRS community & authors of public APRS documentation
- Leaflet and open-map ecosystem contributors
