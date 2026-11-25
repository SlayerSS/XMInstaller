# -*- coding: utf-8 -*-
"""Минимальный клиент DVRIP/Sofia для регистраторов Xiongmai."""
import hashlib
import json
import socket
import struct
import threading

OK = {100, 150, 515}

CODES = {
    100: "ok",
    101: "unknown error",
    102: "unsupported version",
    103: "request not allowed",
    105: "user not logged in",
    106: "wrong username or password",
    107: "no permission",
    203: "password incorrect",
    204: "user does not exist",
    205: "user exists",
    208: "illegal input",
    209: "user already exists",
    515: "upgrade successful",
    603: "system restart required",
}


def sofia_hash(password):
    digest = hashlib.md5(password.encode("utf-8")).digest()
    alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"
    return "".join(alphabet[(a + b) % 62] for a, b in zip(digest[::2], digest[1::2]))


class NvrError(Exception):
    def __init__(self, message, code=None, payload=None):
        super().__init__(message)
        self.code = code
        self.payload = payload


class Nvr:
    def __init__(self, host, port, username, password):
        self.host = host
        self.port = int(port)
        self.username = username
        self.password = password
        self.sock = None
        self.session = 0
        self.seq = 0
        self.info = {}
        self._lock = threading.Lock()
        self._alive = None
        self._closed = False

    def connect(self):
        self.sock = socket.create_connection((self.host, self.port), timeout=8)
        self.sock.settimeout(8)
        reply = self.request(1000, {
            "EncryptType": "MD5",
            "LoginType": "DVRIP-Web",
            "PassWord": sofia_hash(self.password),
            "UserName": self.username,
        })
        if reply.get("Ret") not in OK:
            code = reply.get("Ret")
            raise NvrError(CODES.get(code, "вход не выполнен"), code, reply)
        self.session = int(reply.get("SessionID") or "0", 16)
        self.info = {
            "alive": reply.get("AliveInterval"),
            "channels": reply.get("ChannelNum"),
            "deviceType": reply.get("DeviceType ") or reply.get("DeviceType"),
            "ret": reply.get("Ret"),
        }
        interval = int(reply.get("AliveInterval") or 20)
        self._alive = threading.Thread(target=self._keepalive, args=(max(5, interval - 2),), daemon=True)
        self._alive.start()
        return self.info

    def close(self):
        self._closed = True
        try:
            if self.sock:
                self.sock.close()
        except OSError:
            pass
        self.sock = None

    def _keepalive(self, pause):
        while not self._closed:
            threading.Event().wait(pause)
            if self._closed or not self.sock:
                return
            try:
                self.request(1006, {"Name": "KeepAlive", "SessionID": self._sid()})
            except Exception:
                return

    def _sid(self):
        return "0x%08X" % self.session

    def _recv_exact(self, size):
        buf = bytearray()
        while len(buf) < size:
            chunk = self.sock.recv(size - len(buf))
            if not chunk:
                raise NvrError("соединение закрыто")
            buf.extend(chunk)
        return bytes(buf)

    def request(self, code, payload):
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8") + b"\x0a\x00"
        header = struct.pack("BB2xII2xHI", 255, 0, self.session, self.seq, code, len(body))
        with self._lock:
            self.sock.sendall(header + body)
            self.seq += 1
            raw = self._recv_exact(20)
            _, _, session, _, _, length = struct.unpack("BB2xII2xHI", raw)
            if session:
                self.session = session
            data = self._recv_exact(length)
        if data.endswith(b"\x0a\x00"):
            data = data[:-2]
        else:
            data = data.rstrip(b"\x00")
        if not data:
            return {"Ret": 100}
        try:
            return json.loads(data.decode("utf-8"))
        except json.JSONDecodeError:
            return {"Ret": 100, "raw": data.decode("utf-8", "replace")}

    def _ok(self, reply, action):
        code = reply.get("Ret")
        if code not in OK:
            raise NvrError("%s: %s" % (action, CODES.get(code, code)), code, reply)
        return reply

    def get_config(self, name):
        reply = self.request(1042, {"Name": name, "SessionID": self._sid()})
        self._ok(reply, name)
        if name in reply:
            return reply[name]
        return {key: value for key, value in reply.items() if key not in ("Ret", "SessionID", "Name")}

    def system_info(self):
        reply = self.request(1020, {"Name": "SystemInfo", "SessionID": self._sid()})
        self._ok(reply, "SystemInfo")
        return reply.get("SystemInfo") or reply

    def authority(self):
        reply = self._ok(self.request(1470, {"Name": "AuthorityList", "SessionID": self._sid()}), "права")
        return reply.get("AuthorityList") or []

    def groups(self):
        reply = self._ok(self.request(1474, {"Name": "Groups", "SessionID": self._sid()}), "группы")
        return reply.get("Groups") or []

    def users(self):
        reply = self._ok(self.request(1472, {"Name": "Users", "SessionID": self._sid()}), "пользователи")
        return reply.get("Users") or []

    def add_user(self, name, password_hash, group, authority, memo="", sharable=True, reserved=False):
        reply = self.request(1482, {
            "Name": "User",
            "SessionID": self._sid(),
            "User": {
                "AuthorityList": authority,
                "Group": group,
                "Memo": memo or "",
                "Name": name,
                "Password": password_hash,
                "Reserved": bool(reserved),
                "Sharable": bool(sharable),
            },
        })
        return reply

    def modify_user(self, name, group, authority, memo="", sharable=True, reserved=False):
        return self.request(1484, {
            "SessionID": self._sid(),
            "UserName": name,
            "User": {
                "AuthorityList": authority,
                "Group": group,
                "Memo": memo or "",
                "Name": name,
                "Password": "",
                "Reserved": bool(reserved),
                "Sharable": bool(sharable),
            },
        })

    def change_password(self, name, new_hash, old_hash):
        return self.request(1488, {
            "EncryptType": "MD5",
            "NewPassWord": new_hash,
            "PassWord": old_hash or "",
            "SessionID": self._sid(),
            "UserName": name,
        })

    def delete_user(self, name):
        return self.request(1486, {
            "Name": name,
            "SessionID": self._sid(),
        })

    def logs(self, begin="2020-01-01 00:00:00", end="2035-12-31 23:59:59"):
        reply = self.request(1442, {
            "Name": "OPLogQuery",
            "SessionID": self._sid(),
            "OPLogQuery": {
                "BeginTime": begin,
                "EndTime": end,
                "Type": "LogAll",
            },
        })
        return reply
