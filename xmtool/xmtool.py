# -*- coding: utf-8 -*-
"""Локальная утилита установщика регистраторов Xiongmai."""
import json
import os
import threading
import webbrowser
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from cfgfile import Cfg, apply_transfer, ip_text, map_authority, sofia_hash
from nvr import CODES, Nvr, NvrError, OK

HOST = "127.0.0.1"
PORT = 8765
ROOT = os.path.dirname(os.path.abspath(__file__))
STATE = {"file": None, "device": None, "lock": threading.Lock()}


def device_snapshot(nvr: Nvr):
    info = nvr.system_info()
    common = nvr.get_config("NetWork.NetCommon")
    try:
        dns = nvr.get_config("NetWork.NetDNS")
    except NvrError:
        dns = {}
    try:
        ntp = nvr.get_config("NetWork.NetNTP")
    except NvrError:
        ntp = {}
    remote = nvr.get_config("NetWork.RemoteDeviceV3")
    try:
        widgets = nvr.get_config("AVEnc.VideoWidget")
    except NvrError:
        widgets = []
    cameras = []
    for index, slot in enumerate(remote or []):
        decoders = slot.get("Decoder") or []
        if not isinstance(decoders, list):
            decoders = []
        try:
            active_index = int(str(slot.get("SingleConnId") or "0"), 16) - 1
        except ValueError:
            active_index = 0
        active = decoders[active_index] if 0 <= active_index < len(decoders) else (decoders[0] if decoders else {})
        title = ""
        if index < len(widgets or []):
            title = ((widgets[index] or {}).get("ChannelTitle") or {}).get("Name") or ""
        cameras.append({
            "channel": index + 1,
            "enable": bool(slot.get("Enable")),
            "title": title,
            "ip": active.get("IPAddress") or "",
            "protocol": active.get("Protocol") or "",
            "port": active.get("Port") or "",
            "user": active.get("UserName") or "",
            "profiles": len(decoders),
        })
    try:
        records_raw = nvr.get_config("Record")
    except NvrError:
        records_raw = []
    records = []
    for index, item in enumerate(records_raw or []):
        mask = period = ""
        try:
            mask = item["Mask"][0][0]
            period = item["TimeSection"][0][0]
        except (KeyError, IndexError, TypeError):
            pass
        records.append({
            "channel": index + 1,
            "mode": item.get("RecordMode") or "",
            "mask": mask,
            "period": period,
        })
    motion = []
    try:
        detect = nvr.get_config("Detect.MotionDetect")
        for index, item in enumerate(detect or []):
            motion.append({
                "channel": index + 1,
                "MotionDetect": bool(item.get("Enable")),
                "MotionDetectLevel": item.get("Level"),
            })
    except NvrError:
        pass
    users = []
    for item in nvr.users():
        users.append({
            "name": item.get("Name") or "",
            "group": item.get("Group") or "",
            "memo": item.get("Memo") or "",
            "password": item.get("Password") or "",
            "passwordHint": {"tlJwpbo6": "пустой пароль", "OxhlwSG8": "tluafed"}.get(item.get("Password") or "", ""),
            "reserved": bool(item.get("Reserved")),
            "sharable": bool(item.get("Sharable")),
            "authority": list(item.get("AuthorityList") or []),
        })
    log_reply = nvr.logs()
    logs = []
    for item in log_reply.get("OPLogQuery") or []:
        if isinstance(item, dict):
            logs.append({
                "file": "регистратор",
                "time": item.get("Time") or "",
                "kind": item.get("User") or "",
                "event": item.get("Type") or "",
                "data": item.get("Data") or "",
            })
    runtime = info.get("DeviceRunTime")
    try:
        runtime_text = "%s сек" % int(str(runtime), 16)
    except (TypeError, ValueError):
        runtime_text = str(runtime or "")
    summary = {
        "file": "%s:%s" % (nvr.host, nvr.port),
        "generation": "новый" if str(info.get("SoftWareVersion") or "").startswith("V4.03") else "подключён",
        "version": info.get("SoftWareVersion") or "",
        "product": info.get("HardWare") or "",
        "serial": info.get("SerialNo") or "",
        "ip": ip_text(common.get("HostIP")),
        "mac": common.get("MAC") or "",
        "channels": info.get("DigChannel") or len(cameras),
        "cameras": len([row for row in cameras if row["ip"]]),
        "users": [row["name"] for row in users],
        "login": nvr.username,
        "runtime": runtime_text,
        "build": info.get("BuildTime") or "",
        "alarmIn": info.get("AlarmInChannel"),
        "alarmOut": info.get("AlarmOutChannel"),
    }
    network = {
        "ip": ip_text(common.get("HostIP")),
        "mask": ip_text(common.get("Submask")),
        "gateway": ip_text(common.get("GateWay")),
        "dns": ip_text(dns.get("Address")),
        "dns2": ip_text(dns.get("SpareAddress")),
        "mac": common.get("MAC") or "",
        "http": common.get("HttpPort"),
        "tcp": common.get("TCPPort"),
        "udp": common.get("UDPPort"),
        "ntp": bool(ntp.get("Enable")),
        "timezone": ntp.get("TimeZone"),
        "plain": "",
    }
    return {
        "summary": summary,
        "cameras": cameras,
        "users": users,
        "network": network,
        "records": records,
        "motion": motion,
        "logs": logs,
        "sections": [],
    }


def push_users(nvr: Nvr, package):
    live = {item.get("Name"): item for item in nvr.users()}
    groups = {item.get("Name"): item.get("AuthorityList") or [] for item in nvr.groups()}
    known = set(nvr.authority())
    results = []
    for user in package.get("users") or []:
        name = user.get("name") or ""
        if not name or name == nvr.username:
            results.append({"name": name or nvr.username, "result": "пропущен, это текущий вход"})
            continue
        rights = map_authority(user, package.get("groups") or {}, groups, known)
        target_hash = user.get("password") or ""
        group = user.get("group") or "user"
        if name in live:
            reply = nvr.modify_user(name, group, rights, user.get("memo") or "", user.get("sharable", True), live[name].get("Reserved"))
            text = "права %s" % CODES.get(reply.get("Ret"), reply.get("Ret"))
            current_hash = live[name].get("Password") or ""
            if target_hash and current_hash != target_hash:
                pwd = nvr.change_password(name, target_hash, current_hash)
                text += ", пароль %s" % CODES.get(pwd.get("Ret"), pwd.get("Ret"))
            results.append({"name": name, "result": text})
            continue
        reply = nvr.add_user(name, target_hash, group, rights, user.get("memo") or "", user.get("sharable", True), user.get("reserved"))
        if reply.get("Ret") in OK:
            results.append({"name": name, "result": "создан"})
            continue
        mod = nvr.modify_user(name, group, rights, user.get("memo") or "", user.get("sharable", True), True)
        text = "системный, создание %s, права %s" % (
            CODES.get(reply.get("Ret"), reply.get("Ret")),
            CODES.get(mod.get("Ret"), mod.get("Ret")),
        )
        if target_hash:
            old_hash = "tlJwpbo6" if name == "admin" else target_hash
            pwd = nvr.change_password(name, target_hash, old_hash)
            text += ", пароль %s" % CODES.get(pwd.get("Ret"), pwd.get("Ret"))
        results.append({"name": name, "result": text})
    return results


class App(BaseHTTPRequestHandler):
    def log_message(self, fmt, *args):
        return

    def _read(self):
        length = int(self.headers.get("Content-Length") or 0)
        if not length:
            return {}
        raw = self.rfile.read(length)
        return json.loads(raw.decode("utf-8"))

    def _send(self, code, payload, content_type="application/json; charset=utf-8"):
        if not isinstance(payload, (bytes, bytearray)):
            payload = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(payload)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(payload)

    def _error(self, code, message):
        self._send(code, {"error": message})

    def do_GET(self):
        path = self.path.split("?", 1)[0]
        if path in ("/", "/index.html"):
            data = open(os.path.join(ROOT, "static", "index.html"), "rb").read()
            self._send(200, data, "text/html; charset=utf-8")
            return
        if path == "/api/state":
            cfg = STATE["file"]
            dev = STATE["device"]
            self._send(200, {
                "file": cfg.summary() if cfg else None,
                "device": {
                    "host": dev.host,
                    "port": dev.port,
                    "user": dev.username,
                } if dev else None,
            })
            return
        if path == "/api/file":
            if not STATE["file"]:
                self._error(404, "конфиг не открыт")
                return
            self._send(200, STATE["file"].snapshot())
            return
        if path.startswith("/api/file/section"):
            if not STATE["file"]:
                self._error(404, "конфиг не открыт")
                return
            query = self.path.split("path=", 1)[-1]
            from urllib.parse import unquote
            name = unquote(query)
            if name not in STATE["file"].blobs:
                self._error(404, "раздел не найден")
                return
            self._send(200, {"path": name, "text": STATE["file"].section_text(name)})
            return
        if path == "/api/file/export":
            if not STATE["file"]:
                self._error(404, "конфиг не открыт")
                return
            body = json.dumps(STATE["file"].transfer(), ensure_ascii=False, indent=2).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Disposition", "attachment; filename=transfer.xmt.json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
            return
        if path == "/api/device":
            if not STATE["device"]:
                self._error(404, "регистратор не подключён")
                return
            try:
                self._send(200, device_snapshot(STATE["device"]))
            except NvrError as exc:
                self._error(502, str(exc))
            return
        self._error(404, "не найдено")

    def do_POST(self):
        path = self.path.split("?", 1)[0]
        try:
            body = self._read()
        except json.JSONDecodeError:
            self._error(400, "неверный json")
            return
        try:
            if path == "/api/file/open":
                import base64
                raw = base64.b64decode(body.get("data") or "")
                STATE["file"] = Cfg.from_bytes(body.get("name") or "config.cfg", raw)
                self._send(200, STATE["file"].snapshot())
                return
            if path == "/api/file/user":
                if not STATE["file"]:
                    self._error(404, "конфиг не открыт")
                    return
                user = STATE["file"].update_user(body.get("name"), body)
                self._send(200, user)
                return
            if path == "/api/device/login":
                if STATE["device"]:
                    STATE["device"].close()
                    STATE["device"] = None
                nvr = Nvr(body.get("host"), int(body.get("port") or 34567), body.get("user") or "admin", body.get("password") or "")
                nvr.connect()
                STATE["device"] = nvr
                self._send(200, device_snapshot(nvr))
                return
            if path == "/api/device/logout":
                if STATE["device"]:
                    STATE["device"].close()
                STATE["device"] = None
                self._send(200, {"ok": True})
                return
            if path == "/api/device/user":
                nvr = STATE["device"]
                if not nvr:
                    self._error(404, "регистратор не подключён")
                    return
                action = body.get("action")
                name = body.get("name") or ""
                if action == "delete":
                    reply = nvr.delete_user(name)
                elif action == "password":
                    reply = nvr.change_password(name, sofia_hash(body.get("password") or ""), body.get("oldHash") or "")
                elif action == "add":
                    groups = {item.get("Name"): item.get("AuthorityList") or [] for item in nvr.groups()}
                    rights = body.get("authority") or groups.get(body.get("group") or "user") or []
                    reply = nvr.add_user(
                        name,
                        sofia_hash(body.get("password") or ""),
                        body.get("group") or "user",
                        rights,
                        body.get("memo") or "",
                        body.get("sharable", True),
                    )
                else:
                    reply = nvr.modify_user(
                        name,
                        body.get("group") or "user",
                        body.get("authority") or [],
                        body.get("memo") or "",
                        body.get("sharable", True),
                        body.get("reserved", False),
                    )
                if reply.get("Ret") not in OK:
                    self._error(400, CODES.get(reply.get("Ret"), str(reply.get("Ret"))))
                    return
                self._send(200, {"ok": True, "ret": reply.get("Ret")})
                return
            if path == "/api/transfer/users":
                if not STATE["file"] or not STATE["device"]:
                    self._error(400, "нужны открытый конфиг и подключение к регистратору")
                    return
                results = push_users(STATE["device"], STATE["file"].transfer())
                self._send(200, {"results": results})
                return
            if path == "/api/transfer/cfg":
                if not STATE["file"]:
                    self._error(400, "сначала откройте исходный конфиг")
                    return
                import base64
                target = Cfg.from_bytes(body.get("name") or "target.cfg", base64.b64decode(body.get("data") or ""))
                built = apply_transfer(target, STATE["file"].transfer(), body.get("options") or {})
                self._send(200, {"name": "migrated.cfg", "data": base64.b64encode(built).decode("ascii")})
                return
        except NvrError as exc:
            self._error(502, str(exc))
            return
        except KeyError:
            self._error(404, "пользователь не найден")
            return
        except Exception as exc:
            self._error(500, str(exc))
            return
        self._error(404, "не найдено")


def main():
    os.chdir(ROOT)
    server = ThreadingHTTPServer((HOST, PORT), App)
    url = "http://%s:%s/" % (HOST, PORT)
    print("XM установщик: %s" % url)
    threading.Timer(0.6, lambda: webbrowser.open(url)).start()
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        if STATE["device"]:
            STATE["device"].close()
        server.server_close()


if __name__ == "__main__":
    main()
