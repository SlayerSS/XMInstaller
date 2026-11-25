# -*- coding: utf-8 -*-
"""Чтение, разбор и сборка cfg-архивов регистраторов Xiongmai."""
import copy
import gzip
import io
import json
import re
import zipfile
from datetime import datetime, timezone

LOG_LINE = re.compile(r"^(\d+-\d+-\d+ \d+:\d+:\d+)\[([^\]]+)\],\[([^\]]*)\],\[(.*)\]$")
KNOWN_PASSWORDS = {
    "tlJwpbo6": "пустой пароль",
    "OxhlwSG8": "tluafed",
}


def ip_text(value):
    if not value:
        return ""
    text = str(value)
    if text.lower().startswith("0x"):
        number = int(text, 16)
        return ".".join(str((number >> (8 * i)) & 255) for i in range(4))
    return text


def ip_hex(value):
    parts = [int(item) for item in str(value).split(".")]
    number = parts[0] | (parts[1] << 8) | (parts[2] << 16) | (parts[3] << 24)
    return "0x%08X" % number


def _loads(raw):
    if raw[:2] == b"\x1f\x8b":
        raw = gzip.GzipFile(fileobj=io.BytesIO(raw)).read()
    try:
        return json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None


def _dumps(obj):
    text = json.dumps(obj, ensure_ascii=False, indent=3, separators=(",", " : "))
    if not text.endswith("\n"):
        text += "\n"
    return text.encode("utf-8")


def _gzip(obj):
    payload = _dumps(obj)
    buf = io.BytesIO()
    with gzip.GzipFile(fileobj=buf, mode="wb", mtime=0, filename="") as handle:
        handle.write(payload)
    return buf.getvalue()


def _text(raw):
    return raw.decode("utf-8", "replace")


class Cfg:
    def __init__(self, name, blobs):
        self.name = name
        self.blobs = blobs
        self.json = {}
        for path, raw in blobs.items():
            if path.startswith("Json/") or path in ("Account1", "Account2"):
                parsed = _loads(raw)
                if isinstance(parsed, (dict, list)):
                    self.json[path] = parsed
        self.users = self._read_users()
        account = self.json.get("Account1") or self.json.get("Account2") or {}
        self.groups = {
            item.get("Name"): list(item.get("AuthorityList") or [])
            for item in account.get("Groups") or []
            if item.get("Name")
        }

    @classmethod
    def from_bytes(cls, name, data):
        blobs = {}
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            for info in archive.infolist():
                if info.is_dir():
                    continue
                blobs[info.filename.replace("\\", "/")] = archive.read(info)
        return cls(name, blobs)

    def _read_users(self):
        account = self.json.get("Account1") or self.json.get("Account2") or {}
        users = []
        for item in account.get("Users") or []:
            users.append({
                "name": item.get("Name") or "",
                "group": item.get("Group") or "",
                "memo": item.get("Memo") or "",
                "password": item.get("Password") or "",
                "passwordHint": KNOWN_PASSWORDS.get(item.get("Password") or "", ""),
                "reserved": bool(item.get("Reserved")),
                "sharable": bool(item.get("Sharable")),
                "authority": list(item.get("AuthorityList") or []),
            })
        return users

    def generation(self):
        version = _text(self.blobs.get("Version.ini", b"")).strip()
        dhcp = _text(self.blobs.get("dhcp.cfg", b"")).strip()
        account = _text(self.blobs.get("Account1", b""))
        if "PasswordV2" in account or version.startswith("Version: V4.03") or dhcp.startswith("DhcpEnable"):
            kind = "новый"
        elif version.startswith("Version: V4.02") or dhcp in ("0", "1"):
            kind = "старый"
        else:
            kind = "неизвестный"
        return kind, version, dhcp

    def _net(self):
        network = (self.json.get("Json/NetWork") or {}).get("NetCommon") or {}
        dns = (self.json.get("Json/NetWork") or {}).get("NetDNS") or {}
        ntp = (self.json.get("Json/NetWork") or {}).get("NetNTP") or {}
        dhcp = (self.json.get("Json/NetWork") or {}).get("NetDHCP") or []
        return network, dns, ntp, dhcp

    def _titles(self):
        widgets = (self.json.get("Json/AVEnc") or {}).get("VideoWidget") or []
        titles = []
        for item in widgets:
            title = item.get("ChannelTitle") or {}
            titles.append(title.get("Name") or "")
        return titles

    def cameras(self):
        remote = (self.json.get("Json/NetWork") or {}).get("RemoteDeviceV3") or []
        titles = self._titles()
        rows = []
        for index, slot in enumerate(remote):
            decoders = slot.get("Decoder") or []
            if not isinstance(decoders, list):
                decoders = []
            try:
                active_index = int(str(slot.get("SingleConnId") or "0"), 16) - 1
            except ValueError:
                active_index = 0
            profiles = []
            for dec in decoders:
                if not isinstance(dec, dict):
                    continue
                profiles.append({
                    "ip": dec.get("IPAddress") or "",
                    "protocol": dec.get("Protocol") or "",
                    "port": dec.get("Port") or 0,
                    "user": dec.get("UserName") or "",
                    "password": dec.get("PassWord") or "",
                    "stream": dec.get("StreamType") or "",
                    "name": dec.get("ConfName") or "",
                    "enable": bool(dec.get("Enable")),
                    "channel": dec.get("Channel") or 0,
                })
            active = profiles[active_index] if 0 <= active_index < len(profiles) else (profiles[0] if profiles else {})
            rows.append({
                "channel": index + 1,
                "enable": bool(slot.get("Enable")),
                "title": titles[index] if index < len(titles) else "",
                "active": active_index + 1 if profiles else 0,
                "ip": active.get("ip", ""),
                "protocol": active.get("protocol", ""),
                "port": active.get("port", ""),
                "user": active.get("user", ""),
                "profiles": profiles,
            })
        return rows

    def network(self):
        common, dns, ntp, dhcp_rows = self._net()
        plain = _text(self.blobs.get("network", b""))
        return {
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
            "dhcp": dhcp_rows,
            "plain": plain.strip(),
        }

    def records(self):
        rows = self.json.get("Json/Record") or []
        out = []
        for index, item in enumerate(rows):
            mask = ""
            period = ""
            try:
                mask = item["Mask"][0][0]
                period = item["TimeSection"][0][0]
            except (KeyError, IndexError, TypeError):
                pass
            out.append({
                "channel": index + 1,
                "mode": item.get("RecordMode") or "",
                "mask": mask,
                "period": period,
                "pre": item.get("PreRecord"),
                "packet": item.get("PacketLength"),
            })
        return out

    def motion(self):
        detect = self.json.get("Json/Detect") or {}
        out = []
        for key, label in (("MotionDetect", "движение"), ("LossDetect", "потеря"), ("BlindDetect", "засвет")):
            for index, item in enumerate(detect.get(key) or []):
                if len(out) <= index:
                    out.append({"channel": index + 1})
                out[index][key] = bool(item.get("Enable"))
                if "Level" in item:
                    out[index][key + "Level"] = item.get("Level")
                out[index]["label"] = label
        return out

    def logs(self, limit=500):
        rows = []
        for path in sorted(self.blobs):
            if not path.lower().startswith("log") or not path.lower().endswith(".ini"):
                continue
            for line in _text(self.blobs[path]).splitlines():
                match = LOG_LINE.match(line.strip())
                if not match:
                    if line.strip():
                        rows.append({"file": path, "time": "", "kind": "", "event": "", "data": line.strip()})
                    continue
                rows.append({
                    "file": path,
                    "time": match.group(1),
                    "kind": match.group(2),
                    "event": match.group(3),
                    "data": match.group(4),
                })
        return rows[-limit:]

    def sections(self):
        rows = []
        for path, raw in sorted(self.blobs.items()):
            kind = "json" if path in self.json else "text" if path.startswith("Log") or path.endswith((".ini", ".conf", ".cfg")) or path in ("network", "Account1", "Account2") else "binary"
            rows.append({"path": path, "size": len(raw), "kind": kind})
        return rows

    def section_text(self, path, limit=120000):
        if path in self.json:
            text = json.dumps(self.json[path], ensure_ascii=False, indent=2)
        else:
            raw = self.blobs.get(path, b"")
            if raw[:2] == b"\x1f\x8b":
                try:
                    raw = gzip.GzipFile(fileobj=io.BytesIO(raw)).read()
                except OSError:
                    pass
            text = raw.decode("utf-8", "replace")
        if len(text) > limit:
            return text[:limit] + "\n\n… обрезано, слишком большой раздел"
        return text

    def summary(self):
        kind, version, dhcp = self.generation()
        short = ""
        for line in version.splitlines():
            if line.startswith("Version:"):
                short = line.split(":", 1)[1].strip()
        common, _, _, _ = self._net()
        product = _text(self.blobs.get("ProductType.ini", b"")).strip()
        serial = _text(self.blobs.get("SerialNumber", b"")).strip()
        cameras = self.cameras()
        bound = [row for row in cameras if row["ip"]]
        return {
            "file": self.name,
            "generation": kind,
            "version": short or version.strip(),
            "product": product,
            "serial": serial,
            "dhcpFile": dhcp,
            "ip": ip_text(common.get("HostIP")),
            "mac": common.get("MAC") or "",
            "channels": len(cameras),
            "cameras": len(bound),
            "users": [item["name"] for item in self.users],
        }

    def snapshot(self):
        return {
            "summary": self.summary(),
            "cameras": self.cameras(),
            "users": self.users,
            "network": self.network(),
            "records": self.records(),
            "motion": self.motion(),
            "logs": self.logs(),
            "sections": self.sections(),
        }

    def transfer(self):
        kind, version, _ = self.generation()
        return {
            "format": "xm-transfer",
            "version": 1,
            "exportedAt": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
            "source": self.summary() | {"generation": kind, "firmware": version},
            "network": self.network(),
            "cameras": self.cameras(),
            "users": self.users,
            "groups": self.groups,
            "records": self.json.get("Json/Record") or [],
            "detect": {
                key: [
                    {
                        "Enable": item.get("Enable"),
                        "Level": item.get("Level"),
                        "Region": item.get("Region"),
                        "EventHandler": item.get("EventHandler"),
                    }
                    for item in (self.json.get("Json/Detect") or {}).get(key) or []
                ]
                for key in ("MotionDetect", "LossDetect", "BlindDetect")
            },
        }

    def update_user(self, name, changes):
        for user in self.users:
            if user["name"] != name:
                continue
            for key in ("group", "memo", "sharable", "authority"):
                if key in changes:
                    user[key] = changes[key]
            if changes.get("password"):
                user["password"] = sofia_hash(changes["password"])
                user["passwordHint"] = ""
            return user
        raise KeyError(name)


def sofia_hash(password):
    import hashlib
    digest = hashlib.md5(password.encode("utf-8")).digest()
    alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
    return "".join(alphabet[(a + b) % 62] for a, b in zip(digest[::2], digest[1::2]))


def _overlay(dst, src):
    if isinstance(dst, dict) and isinstance(src, dict):
        for key, value in src.items():
            if key not in dst or value is None:
                continue
            dst[key] = _overlay(dst[key], value)
        return dst
    if isinstance(dst, list) and isinstance(src, list):
        if len(dst) == len(src):
            if dst and isinstance(dst[0], (dict, list)) and isinstance(src[0], (dict, list)):
                return [_overlay(copy.deepcopy(item), src[index]) for index, item in enumerate(dst)]
            return copy.deepcopy(src)
        out = copy.deepcopy(dst)
        for index in range(min(len(dst), len(src))):
            out[index] = _overlay(out[index], src[index])
        return out
    if dst is None or src is None or type(dst) is not type(src):
        return dst
    return copy.deepcopy(src)


def _blank_decoder():
    return {
        "Channel": 0,
        "ConfName": "",
        "DevType": "IPC",
        "Enable": False,
        "IPAddress": "",
        "Interval": 10,
        "MacAddr": "",
        "MainRtspUrl": "",
        "PassWord": "",
        "Port": 34567,
        "Protocol": "TCP",
        "SerialNo": "",
        "StreamType": "MAIN",
        "SubRtspUrl": "",
        "TransModel": 0,
        "UserName": "",
    }


def apply_transfer(target: Cfg, package, options):
    """Собрать новый cfg на базе целевого аппарата. Учётки в файл не пишутся."""
    blobs = dict(target.blobs)
    network = copy.deepcopy(target.json.get("Json/NetWork") or {})
    if options.get("cameras"):
        remote = network.get("RemoteDeviceV3") or []
        incoming = package.get("cameras") or []
        for index, row in enumerate(incoming):
            if index >= len(remote):
                break
            profiles = row.get("profiles") or []
            decoders = []
            for profile in profiles:
                dec = _blank_decoder()
                dec.update({
                    "Enable": bool(profile.get("enable", True)),
                    "IPAddress": profile.get("ip") or "",
                    "Protocol": profile.get("protocol") or "TCP",
                    "Port": int(profile.get("port") or 34567),
                    "UserName": profile.get("user") or "",
                    "PassWord": profile.get("password") or "",
                    "StreamType": profile.get("stream") or "MAIN",
                    "ConfName": profile.get("name") or "",
                    "Channel": int(profile.get("channel") or 0),
                })
                decoders.append(dec)
            slot = {
                "ConnType": "SINGLE",
                "Decoder": decoders or None,
                "EnCheckTime": True if decoders else False,
                "Enable": bool(row.get("enable")),
                "SingleConnId": "0x%08X" % (int(row.get("active") or (1 if decoders else 0))),
                "SynchResolution": False,
                "TourIntv": 10,
            }
            remote[index] = slot
        network["RemoteDeviceV3"] = remote
    if options.get("network"):
        src = package.get("network") or {}
        common = network.setdefault("NetCommon", {})
        mac = common.get("MAC")
        if src.get("ip"):
            common["HostIP"] = ip_hex(src["ip"])
        if src.get("mask"):
            common["Submask"] = ip_hex(src["mask"])
        if src.get("gateway"):
            common["GateWay"] = ip_hex(src["gateway"])
        for key, field in (("HttpPort", "http"), ("TCPPort", "tcp"), ("UDPPort", "udp")):
            if src.get(field) not in (None, ""):
                common[key] = int(src[field])
        if mac:
            common["MAC"] = mac
        dns = network.setdefault("NetDNS", {})
        if src.get("dns"):
            dns["Address"] = ip_hex(src["dns"])
        if src.get("dns2"):
            dns["SpareAddress"] = ip_hex(src["dns2"])
        if "NetNTP" in network and "ntp" in src:
            network["NetNTP"]["Enable"] = bool(src["ntp"])
            if src.get("timezone") is not None:
                network["NetNTP"]["TimeZone"] = src["timezone"]
        if isinstance(network.get("NetDHCP"), list):
            for item in network["NetDHCP"]:
                if item.get("Interface") == "eth0":
                    item["Enable"] = False
        if isinstance(network.get("ForceNetDHCP"), dict):
            network["ForceNetDHCP"]["Enable"] = False
        blobs["network"] = ("HOSTIP = %s\nSUBMASK = %s\nGATEWAYIP = %s\n" % (
            src.get("ip") or ip_text(common.get("HostIP")),
            src.get("mask") or "255.255.255.0",
            src.get("gateway") or ip_text(common.get("GateWay")),
        )).encode("ascii")
        resolv = ("nameserver %s\nnameserver %s\n" % (src.get("dns") or "8.8.8.8", src.get("dns2") or "8.8.8.8")).encode("ascii")
        for path in ("resolv.conf", "resolv_eth0.conf", "eth0.conf"):
            if path in blobs:
                blobs[path] = resolv
        if "dhcp.cfg" in blobs:
            current = blobs["dhcp.cfg"].decode("utf-8", "replace")
            blobs["dhcp.cfg"] = (b"DhcpEnable:0" if current.startswith("Dhcp") else b"0")
    blobs["Json/NetWork"] = _gzip(network)
    if "Json/NetWork.second" in blobs:
        blobs["Json/NetWork.second"] = blobs["Json/NetWork"]

    if options.get("titles"):
        av = copy.deepcopy(target.json.get("Json/AVEnc") or {})
        widgets = av.get("VideoWidget") or []
        for index, row in enumerate(package.get("cameras") or []):
            if index < len(widgets) and row.get("title") is not None:
                widgets[index].setdefault("ChannelTitle", {})["Name"] = row["title"]
        av["VideoWidget"] = widgets
        blobs["Json/AVEnc"] = _gzip(av)
        if "Json/AVEnc.second" in blobs:
            blobs["Json/AVEnc.second"] = blobs["Json/AVEnc"]

    if options.get("record") and package.get("records"):
        count = len((target.json.get("Json/Record") or package["records"]))
        if target.json.get("Json/AVEnc", {}).get("VideoWidget"):
            count = max(count, 0)
        remote_len = len((target.json.get("Json/NetWork") or {}).get("RemoteDeviceV3") or [])
        if remote_len:
            count = remote_len
        rows = [copy.deepcopy(item) for item in package["records"][:count]]
        while len(rows) < count:
            rows.append(copy.deepcopy(rows[-1]))
        blobs["Json/Record"] = _gzip(rows)
        blobs["Json/Record.second"] = blobs["Json/Record"]

    if options.get("detect") and package.get("detect"):
        detect = copy.deepcopy(target.json.get("Json/Detect") or {})
        for key, rows in package["detect"].items():
            if not isinstance(detect.get(key), list):
                continue
            for index, src in enumerate(rows):
                if index >= len(detect[key]) or not isinstance(src, dict):
                    continue
                detect[key][index] = _overlay(detect[key][index], src)
        blobs["Json/Detect"] = _gzip(detect)
        if "Json/Detect.second" in blobs:
            blobs["Json/Detect.second"] = blobs["Json/Detect"]

    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", compression=zipfile.ZIP_DEFLATED) as archive:
        for path in sorted(blobs):
            archive.writestr(path, blobs[path])
    return buf.getvalue()


def map_authority(user, source_groups, target_groups, known):
    current = set(user.get("authority") or [])
    admin = set((source_groups or {}).get("admin") or [])
    plain = set((source_groups or {}).get("user") or [])
    if admin and current == admin and target_groups.get("admin"):
        return list(target_groups["admin"])
    if plain and current == plain and target_groups.get("user"):
        return list(target_groups["user"])
    if known:
        return [item for item in user.get("authority") or [] if item in known]
    return list(user.get("authority") or [])
