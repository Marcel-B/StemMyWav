"""Gateway integration test using a transiently failing local Mac API stub."""

import io
import json
import os
from pathlib import Path
import socket
import subprocess
import tempfile
import threading
import time
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
from zipfile import ZipFile


ROOT = Path(__file__).resolve().parents[1]
GATEWAY = ROOT / "StemMyWav.Gateway/bin/Debug/net10.0/StemMyWav.Gateway.dll"


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def request(url, method="GET", body=None, key=None, content_type=None):
    headers = {}
    if key is not None:
        headers["X-Api-Key"] = key
    if content_type is not None:
        headers["Content-Type"] = content_type
    call = Request(url, data=body, headers=headers, method=method)
    try:
        with urlopen(call, timeout=5) as response:
            return response.status, response.read()
    except HTTPError as error:
        return error.code, error.read()


class MacStub(BaseHTTPRequestHandler):
    attempts = 0

    def do_POST(self):
        self.__class__.attempts += 1
        body = self.rfile.read(int(self.headers["Content-Length"]))
        assert self.headers["X-Api-Key"] == "mac-test-key"
        assert self.headers["Content-Type"] == "audio/flac"
        assert body.startswith(b"fLaC")
        assert self.path == "/api/separate?dereverb=true"
        if self.__class__.attempts == 1:
            self.send_response(503)
            self.end_headers()
            return

        stream = io.BytesIO()
        with ZipFile(stream, "w") as archive:
            for name in ("vocals.wav", "instrumental.wav", "vocals_dry.wav", "vocals_reverb.wav"):
                archive.writestr(name, b"RIFFtest")
        payload = stream.getvalue()
        self.send_response(200)
        self.send_header("Content-Type", "application/zip")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args):
        pass


class GatewayIntegrationTest(unittest.TestCase):
    def test_auth_validation_retry_and_result(self):
        MacStub.attempts = 0
        with tempfile.TemporaryDirectory() as work:
            path = Path(work)
            (path / "gateway-key").write_text("gateway-test-key\n")
            (path / "mac-key").write_text("mac-test-key\n")
            mac_port, gateway_port = free_port(), free_port()
            mac = ThreadingHTTPServer(("127.0.0.1", mac_port), MacStub)
            server_thread = threading.Thread(target=mac.serve_forever, daemon=True)
            server_thread.start()
            env = os.environ.copy()
            env.update({
                "ASPNETCORE_ENVIRONMENT": "Development",
                "ASPNETCORE_URLS": f"http://127.0.0.1:{gateway_port}",
                "Gateway__ApiKeyFile": str(path / "gateway-key"),
                "Gateway__DataDirectory": str(path / "data"),
                "MacBackend__Url": f"http://127.0.0.1:{mac_port}",
                "MacBackend__ApiKeyFile": str(path / "mac-key"),
            })
            gateway = subprocess.Popen(["dotnet", str(GATEWAY)], cwd=ROOT, env=env,
                                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            base = f"http://127.0.0.1:{gateway_port}"
            try:
                for _ in range(50):
                    try:
                        if request(base + "/health")[0] == 200:
                            break
                    except URLError:
                        time.sleep(0.1)
                else:
                    self.fail("Gateway did not start")

                status, payload = request(base + "/openapi/v1.json")
                self.assertEqual(200, status)
                specification = json.loads(payload)
                self.assertEqual("StemMyWav Gateway API", specification["info"]["title"])
                self.assertEqual("apiKey", specification["components"]["securitySchemes"]["GatewayApiKey"]["type"])
                self.assertEqual("X-Api-Key", specification["components"]["securitySchemes"]["GatewayApiKey"]["name"])
                self.assertEqual({"GatewayApiKey": []}, specification["paths"]["/api/jobs"]["post"]["security"][0])
                self.assertEqual("binary", specification["components"]["schemas"]["Stream"]["format"])
                self.assertEqual("#/components/schemas/Stream", specification["paths"]["/api/jobs"]["post"]["requestBody"]["content"]["audio/flac"]["schema"]["$ref"])
                self.assertEqual("#/components/schemas/Stream", specification["paths"]["/api/jobs/{id}/result"]["get"]["responses"]["200"]["content"]["application/zip"]["schema"]["$ref"])
                self.assertEqual(200, request(base + "/swagger/index.html")[0])

                self.assertEqual(401, request(base + "/api/jobs/00000000-0000-0000-0000-000000000000")[0])
                self.assertEqual(400, request(base + "/api/jobs", "POST", b"invalid", "gateway-test-key", "audio/flac")[0])
                status, payload = request(base + "/api/jobs?dereverb=true", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
                self.assertEqual(202, status)
                job_id = json.loads(payload)["id"]
                self.assertTrue((path / "data" / job_id / "input.flac").exists())

                deadline = time.monotonic() + 50
                while time.monotonic() < deadline:
                    status, payload = request(base + f"/api/jobs/{job_id}", key="gateway-test-key")
                    self.assertEqual(200, status)
                    job = json.loads(payload)
                    if job["status"] == "completed":
                        break
                    self.assertNotEqual("failed", job["status"])
                    time.sleep(0.25)
                else:
                    self.fail("Job did not recover from transient Mac error")

                self.assertEqual(2, job["attempts"])
                self.assertEqual(2, MacStub.attempts)
                self.assertFalse((path / "data" / job_id / "input.flac").exists())
                status, payload = request(base + f"/api/jobs/{job_id}/result", key="gateway-test-key")
                self.assertEqual(200, status)
                with ZipFile(io.BytesIO(payload)) as archive:
                    self.assertEqual({"vocals.wav", "instrumental.wav", "vocals_dry.wav", "vocals_reverb.wav"}, set(archive.namelist()))
                self.assertEqual(401, request(base + f"/api/jobs/{job_id}/result")[0])
                self.assertEqual(204, request(base + f"/api/jobs/{job_id}", "DELETE", key="gateway-test-key")[0])
                self.assertFalse((path / "data" / job_id).exists())
                self.assertEqual(404, request(base + f"/api/jobs/{job_id}", key="gateway-test-key")[0])
            finally:
                gateway.terminate()
                gateway.wait(timeout=10)
                mac.shutdown()
                mac.server_close()


if __name__ == "__main__":
    unittest.main()
