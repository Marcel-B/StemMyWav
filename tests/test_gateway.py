"""Gateway integration tests using local Mac API stubs."""

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
from contextlib import contextmanager
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
    status, payload, _ = request_full(url, method, body, key, content_type)
    return status, payload


def request_full(url, method="GET", body=None, key=None, content_type=None):
    headers = {}
    if key is not None:
        headers["X-Api-Key"] = key
    if content_type is not None:
        headers["Content-Type"] = content_type
    call = Request(url, data=body, headers=headers, method=method)
    try:
        with urlopen(call, timeout=5) as response:
            return response.status, response.read(), response.headers
    except HTTPError as error:
        return error.code, error.read(), error.headers


@contextmanager
def run_gateway(handler, **settings):
    """Runs the gateway against a stub Mac API and yields its base URL and data directory."""
    with tempfile.TemporaryDirectory() as work:
        path = Path(work)
        (path / "gateway-key").write_text("gateway-test-key\n")
        (path / "mac-key").write_text("mac-test-key\n")
        mac_port, gateway_port = free_port(), free_port()
        mac = ThreadingHTTPServer(("127.0.0.1", mac_port), handler)
        threading.Thread(target=mac.serve_forever, daemon=True).start()
        env = os.environ.copy()
        env.update({
            "ASPNETCORE_ENVIRONMENT": "Development",
            "ASPNETCORE_URLS": f"http://127.0.0.1:{gateway_port}",
            "Gateway__ApiKeyFile": str(path / "gateway-key"),
            "Gateway__DataDirectory": str(path / "data"),
            "MacBackend__Url": f"http://127.0.0.1:{mac_port}",
            "MacBackend__ApiKeyFile": str(path / "mac-key"),
        })
        env.update(settings)
        process = subprocess.Popen(["dotnet", str(GATEWAY)], cwd=ROOT, env=env,
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
                raise AssertionError("Gateway did not start")
            yield base, path / "data"
        finally:
            process.terminate()
            process.wait(timeout=10)
            mac.shutdown()
            mac.server_close()


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
                self.assertEqual(["queued", "processing", "completed", "failed"],
                                 specification["components"]["schemas"]["JobStatus"]["enum"])
                self.assertEqual("#/components/schemas/JobStatus",
                                 specification["components"]["schemas"]["JobStatusResponse"]["properties"]["status"]["$ref"])
                self.assertEqual("#/components/schemas/Stream", specification["paths"]["/api/jobs"]["post"]["requestBody"]["content"]["audio/flac"]["schema"]["$ref"])
                self.assertEqual("#/components/schemas/Stream", specification["paths"]["/api/jobs/{id}/result"]["get"]["responses"]["200"]["content"]["application/zip"]["schema"]["$ref"])
                self.assertEqual({"GatewayApiKey": []}, specification["paths"]["/api/jobs"]["get"]["security"][0])
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


class UnauthorizedStub(BaseHTTPRequestHandler):
    """Stands in for a Mac whose API key no longer matches the gateway configuration."""

    attempts = 0

    def do_POST(self):
        self.__class__.attempts += 1
        self.rfile.read(int(self.headers["Content-Length"]))
        self.send_response(401)
        self.end_headers()

    def log_message(self, *_args):
        pass


class RejectingStub(BaseHTTPRequestHandler):
    """Stands in for a Mac that refuses the upload itself, as with a truncated FLAC."""

    def do_POST(self):
        self.rfile.read(int(self.headers["Content-Length"]))
        payload = json.dumps({
            "title": "Bad Request",
            "status": 400,
            "detail": "Die Datei ließ sich nicht lesen; sie ist vermutlich unvollständig oder beschädigt.",
        }).encode()
        self.send_response(400)
        self.send_header("Content-Type", "application/problem+json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

    def log_message(self, *_args):
        pass


class UnreadableUploadTest(unittest.TestCase):
    def test_a_refused_upload_fails_once_and_frees_the_queue(self):
        with run_gateway(RejectingStub, Gateway__MaxPendingJobs="1") as (base, data):
            status, payload = request(base + "/api/jobs", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
            self.assertEqual(202, status)
            job_id = json.loads(payload)["id"]

            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                job = json.loads(request(base + f"/api/jobs/{job_id}", key="gateway-test-key")[1])
                if job["status"] == "failed":
                    break
                time.sleep(0.25)
            else:
                self.fail("Job was retried instead of failing")

            # Der Grund aus der Mac-Antwort steht im Status, nicht nur der Statuscode.
            self.assertIn("unvollständig", job["lastError"])
            self.assertEqual(1, job["attempts"])
            self.assertFalse((data / job_id / "input.flac").exists())

            # Der Platz ist sofort wieder frei.
            self.assertEqual(202, request(base + "/api/jobs", "POST", b"fLaCtest",
                                          "gateway-test-key", "audio/flac")[0])

    def test_listing_shows_jobs_and_supports_cleaning_them_up(self):
        with run_gateway(RejectingStub) as (base, _data):
            self.assertEqual(401, request(base + "/api/jobs")[0])
            self.assertEqual([], json.loads(request(base + "/api/jobs", key="gateway-test-key")[1]))

            ids = []
            for _ in range(2):
                status, payload = request(base + "/api/jobs", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
                self.assertEqual(202, status)
                ids.append(json.loads(payload)["id"])

            status, payload = request(base + "/api/jobs", key="gateway-test-key")
            self.assertEqual(200, status)
            listed = json.loads(payload)
            self.assertEqual(set(ids), {job["id"] for job in listed})
            for job in listed:
                self.assertIn(job["status"], {"queued", "processing", "failed"})
                self.assertIn("attempts", job)

            for job_id in ids:
                deadline = time.monotonic() + 30
                while time.monotonic() < deadline:
                    if request(base + f"/api/jobs/{job_id}", "DELETE", key="gateway-test-key")[0] == 204:
                        break
                    time.sleep(0.25)
                else:
                    self.fail("Job could not be deleted")
            self.assertEqual([], json.loads(request(base + "/api/jobs", key="gateway-test-key")[1]))


class QueueRecoveryTest(unittest.TestCase):
    def test_rejected_key_is_retried_instead_of_discarding_the_upload(self):
        UnauthorizedStub.attempts = 0
        with run_gateway(UnauthorizedStub) as (base, data):
            status, payload = request(base + "/api/jobs", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
            self.assertEqual(202, status)
            job_id = json.loads(payload)["id"]

            deadline = time.monotonic() + 30
            while time.monotonic() < deadline and UnauthorizedStub.attempts < 1:
                time.sleep(0.25)
            time.sleep(1)
            status, payload = request(base + f"/api/jobs/{job_id}", key="gateway-test-key")
            self.assertEqual("queued", json.loads(payload)["status"])
            self.assertTrue((data / job_id / "input.flac").exists())

    def test_queue_full_reports_problem_json_and_a_cancelled_job_frees_the_slot(self):
        UnauthorizedStub.attempts = 0
        with run_gateway(UnauthorizedStub, Gateway__MaxPendingJobs="1") as (base, _data):
            status, payload = request(base + "/api/jobs", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
            self.assertEqual(202, status)
            job_id = json.loads(payload)["id"]

            status, payload, headers = request_full(base + "/api/jobs", "POST", b"fLaCtest",
                                                    "gateway-test-key", "audio/flac")
            self.assertEqual(429, status)
            self.assertEqual("60", headers["Retry-After"])
            self.assertEqual("application/problem+json", headers["Content-Type"].split(";")[0])
            self.assertEqual(429, json.loads(payload)["status"])

            # Die Dateiprüfung liegt an der API-Grenze und entscheidet vor der Kapazität,
            # eine ungültige Datei wird also auch bei voller Warteschlange als solche gemeldet.
            status, payload, headers = request_full(base + "/api/jobs", "POST", b"invalid",
                                                    "gateway-test-key", "audio/flac")
            self.assertEqual(400, status)
            self.assertEqual("application/problem+json", headers["Content-Type"].split(";")[0])
            self.assertEqual("Ungültige FLAC-Datei.", json.loads(payload)["detail"])

            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                if request(base + f"/api/jobs/{job_id}", "DELETE", key="gateway-test-key")[0] == 204:
                    break
                time.sleep(0.25)
            else:
                self.fail("Queued job could not be cancelled")

            self.assertEqual(202, request(base + "/api/jobs", "POST", b"fLaCtest",
                                          "gateway-test-key", "audio/flac")[0])

    def test_unreachable_mac_eventually_fails_the_job(self):
        UnauthorizedStub.attempts = 0
        with run_gateway(UnauthorizedStub, Gateway__MaxQueueHours="0.002") as (base, data):
            status, payload = request(base + "/api/jobs", "POST", b"fLaCtest", "gateway-test-key", "audio/flac")
            job_id = json.loads(payload)["id"]
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                job = json.loads(request(base + f"/api/jobs/{job_id}", key="gateway-test-key")[1])
                if job["status"] == "failed":
                    break
                time.sleep(0.25)
            else:
                self.fail("Job never gave up")
            self.assertIn("nicht erreichbar", job["lastError"])
            self.assertFalse((data / job_id / "input.flac").exists())
            self.assertEqual(204, request(base + f"/api/jobs/{job_id}", "DELETE", key="gateway-test-key")[0])


if __name__ == "__main__":
    unittest.main()
